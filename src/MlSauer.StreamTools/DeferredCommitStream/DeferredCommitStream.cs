using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MlSauer.StreamTools
{
    /// <summary>
    /// Stream that takes an underlying File- or MemoryStream, and stores all writes 
    /// non persistently until the Commit() method is called.
    /// Changes are not saved when calling Dispose()
    /// </summary>
    public class DeferredCommitStream : Stream
    {
        /// <summary>constructor</summary>
        /// <param name="stream">the underlying stream. The Stream must be seek-able and readable. Commit() requires writable (duh!).</param>
        /// <param name="leaveOpen">decides if the underlying stream is disposed when Dispose() or Close() is called</param>
        /// <exception cref="ArgumentNullException">if the underlying stream is null</exception>
        /// <exception cref="ArgumentException">if the underlying stream is unsuitable or disposed</exception>
        public DeferredCommitStream(Stream stream, bool leaveOpen)
        {
            if (stream is null) { throw new ArgumentNullException(nameof(stream)); }
            if (!stream.CanRead && !stream.CanWrite)
            {
                throw new ArgumentException("Underlying stream is already disposed", nameof(stream));
            } else if (!stream.CanSeek || !stream.CanRead)
            {
                throw new ArgumentException("Underlying stream is unsuitable", nameof(stream));
            }
            _Stream = stream;
            _Length = stream.Length;
            _LeaveStreamOpen = leaveOpen;
        }

        /// <summary>if a write occurs a block of this size will be created and stored in memory</summary>
        private const long BLOCK_SIZE = 4096;
        /// <summary>The underlying stream</summary>
        private readonly Stream _Stream;
        /// <summary>decides if the underlying stream is disposed when Dispose() or Close() is called</summary>
        private readonly bool _LeaveStreamOpen;
        /// <summary>
        /// if a write on this stream occurs we store the data in here.
        /// Key is the block-index which corresponds to Position index * BLOCK_SIZE in the underlying stream
        /// </summary>
        private readonly SortedDictionary<long, byte[]> _UnwrittenBlocks = new SortedDictionary<long, byte[]>();

        /// <summary>Determines if we can read.</summary>
        public override bool CanRead => !_DisposedValue && _Stream.CanRead;

        /// <summary>Determines if we can seek.</summary>
        public override bool CanSeek => !_DisposedValue && _Stream.CanSeek;

        /// <summary>Determines if we can write. Note this does not check if the underlying stream is writable.</summary>
        public override bool CanWrite => !_DisposedValue;

        private long _Length;

        /// <summary>Current length of the stream</summary>
        public override long Length
        {
            get
            {
                EnsureNotDisposed();
                return _Length;
            }
        }

        private long _Position;

        /// <inheritdoc />
        public override long Position
        {
            get
            {
                EnsureNotDisposed();
                return _Position;
            }
            set
            {
                EnsureNotDisposed();
                _Position = value;
            }
        }

        /// <summary>Does nothing. If you want to write the changes to the underlying stream use Commit()</summary>
        public override void Flush()
        {
            EnsureNotDisposed();
        }

        /// <summary>
        /// Reads data. If the position has been written to before the data will be retrieved from _UnwrittenBlocks.
        /// Otherwise it will be read from the underlying stream directly.
        /// </summary>
        /// <param name="buffer">the data will be stored here</param>
        /// <param name="offset">offset in the buffer array</param>
        /// <param name="count">number of bytes</param>
        /// <returns></returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            EnsureNotDisposed();

            int bytesRead = 0;

            while (bytesRead < count)
            {
                long blockIdx = GetBlockIdx(Position);
                int positionInBlock = (int)(Position % BLOCK_SIZE);

                long actuallyReadBytes;
                if (!_UnwrittenBlocks.TryGetValue(blockIdx, out var currentBlock))
                {
                    currentBlock = new byte[BLOCK_SIZE];
                    _Stream.Seek(blockIdx * BLOCK_SIZE, SeekOrigin.Begin);
                    actuallyReadBytes = _Stream.Read(currentBlock, 0, (int)BLOCK_SIZE);
                }
                else
                {
                    actuallyReadBytes = BLOCK_SIZE;
                    var currentBlockEndOffset = (blockIdx + 1) * BLOCK_SIZE;
                    if (currentBlockEndOffset > _Length)
                    {
                        actuallyReadBytes = _Length % BLOCK_SIZE;
                    }
                }

                if (currentBlock != null)
                {
                    var bytesToRead = count - bytesRead;
                    int n = Math.Min(bytesToRead, (int)actuallyReadBytes - positionInBlock);

                    Array.Copy(currentBlock, positionInBlock, buffer, offset, n);

                    bytesRead += n;
                    Position += n;
                    offset += n;
                    if (n == 0) { return bytesRead; }
                }
                else
                {
                    break;
                }
            }

            return bytesRead;
        }

        /// <summary>Changes the current Position in the stream</summary>
        /// <param name="offset">the byte offset relative to the origin</param>
        /// <param name="origin">the reference point</param>
        /// <returns>the new Position</returns>
        /// <exception cref="ArgumentException">Invalid origin given</exception>
        /// <exception cref="IOException">If attempted to seek before Position 0</exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            EnsureNotDisposed();

            if (origin == SeekOrigin.Current)
            {
                offset = _Position + offset;
            }
            else if (origin == SeekOrigin.End)
            {
                offset = _Length + offset;
            }
            else if (origin != SeekOrigin.Begin)
            {
                throw new ArgumentException("invalid origin", nameof(origin));
            }

            if (offset < 0)
            {
                throw new IOException("Seek before begin");
            }
            Position = Math.Min(_Length, offset);
            return Position;
        }

        /// <summary>Sets the Length of the stream. Currently shrinking is not supported</summary>
        /// <param name="value">the new size</param>
        /// <exception cref="InvalidOperationException">It was attempted to shrink the stream</exception>
        public override void SetLength(long value)
        {
            EnsureNotDisposed();

            if (value >= _Length)
            {
                EnsureBlockExists(value - 1);

                _Length = value;
            }
            else
            {
                throw new InvalidOperationException("Can only enlarge the stream");
            }
        }

        /// <summary>
        /// Writes data to the current position. The data will be stored as a block of BLOCK_SIZE bytes in _UnwrittenBlocks.
        /// Call Commit() to save the changes to the underlying stream.
        /// </summary>
        /// <param name="buffer">the data to write</param>
        /// <param name="offset">the offset in buffer</param>
        /// <param name="count">the number of bytes to write</param>
        /// <exception cref="ArgumentNullException">buffer was null</exception>
        /// <exception cref="ArgumentOutOfRangeException">count or offset was out of buffer range</exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer is null) { throw new ArgumentNullException(nameof(buffer)); }
            if (offset < 0) { throw new ArgumentOutOfRangeException(nameof(offset)); }
            if (count < 0) { throw new ArgumentOutOfRangeException(nameof(count)); }
            if (buffer.Length - offset < count) { throw new ArgumentOutOfRangeException(nameof(count)); }

            EnsureNotDisposed();

            var endPosition = Position + count;
            if (endPosition >= _Length)
            {
                EnsureBlockExists(endPosition);
            }

            long bytesCopied = 0;

            while (bytesCopied < count)
            {
                long blockIdx = GetBlockIdx(Position);
                long positionInBlock = Position % BLOCK_SIZE;
                long bytesInCurrentBlock = Math.Min(count - bytesCopied, BLOCK_SIZE - positionInBlock);

                byte[] currentUnwrittenBlock = GetBlock(blockIdx);

                Array.Copy(buffer, offset, currentUnwrittenBlock, positionInBlock, bytesInCurrentBlock);

                bytesCopied += bytesInCurrentBlock;
                offset += (int)bytesInCurrentBlock;
                Position += bytesInCurrentBlock;
                if (Position > _Length)
                {
                    _Length = Position;
                }
            }
        }

        /// <summary>
        /// Retrieve block of data either from _UnwrittenBlocks
        /// if it had been written to before or directly from the underlying stream.
        /// Only used for writing since it also stores the date in _UnwrittenBlocks.
        /// </summary>
        /// <param name="blockIdx">the block index corresponds to Position index * BLOCK_SIZE in the underlying stream</param>
        /// <returns>the block</returns>
        private byte[] GetBlock(long blockIdx)
        {
            if (!_UnwrittenBlocks.TryGetValue(blockIdx, out byte[] currentUnwrittenBlock))
            {
                _UnwrittenBlocks[blockIdx] = currentUnwrittenBlock = new byte[BLOCK_SIZE];
                var blockOffset = blockIdx * BLOCK_SIZE;
                _Stream.Seek(blockOffset, SeekOrigin.Begin);
                _Stream.Read(currentUnwrittenBlock, 0, (int)BLOCK_SIZE);
            }

            return currentUnwrittenBlock;
        }

        /// <summary>Makes sure we have a block at that position</summary>
        /// <param name="offset">the position</param>
        private void EnsureBlockExists(long offset)
        {
            var blockIdx = GetBlockIdx(offset);
            var lastWrittenBlockIdx = GetBlockIdx(_Stream.Length - 1) - 1;
            var lastUnwrittenBlockIdx = _UnwrittenBlocks.Keys.LastOrDefault();
            var lastWrittenBlock = Math.Max(lastUnwrittenBlockIdx, lastWrittenBlockIdx);
            for (long i = lastWrittenBlock; i < blockIdx; i++)
            {
                _UnwrittenBlocks[blockIdx] = new byte[BLOCK_SIZE];
            }
        }

        /// <summary>Calculates the block index from any offset</summary>
        /// <param name="offset">the offset</param>
        /// <returns>the block index</returns>
        private static long GetBlockIdx(long offset)
        {
            return offset / BLOCK_SIZE;
        }

        /// <summary>Saves all deferred changes to the underlying stream</summary>
        public void Commit()
        {
            EnsureNotDisposed();
            if (!_Stream.CanWrite)
            {
                throw new InvalidOperationException("underlying stream is read-only");
            }
            foreach (var kvp in _UnwrittenBlocks)
            {
                var offset = kvp.Key * BLOCK_SIZE;
                var blockLength = BLOCK_SIZE;
                if (offset + BLOCK_SIZE > _Length)
                {
                    blockLength = _Length % BLOCK_SIZE;
                }
                _Stream.Seek(offset, SeekOrigin.Begin);
                _Stream.Write(kvp.Value, 0, (int)blockLength);
            }
            _UnwrittenBlocks.Clear();
            Debug.Assert(_Length == _Stream.Length);
        }

        /// <summary>Make sure we are not disposed or else throw an ObjectDisposedException</summary>
        /// <exception cref="ObjectDisposedException">if we are disposed</exception>
        private void EnsureNotDisposed()
        {
            if (_DisposedValue) { throw new ObjectDisposedException(nameof(DeferredCommitStream) + " has been disposed"); }
        }

        /// <summary>After this is set to true none of the other methods should work.</summary>
        private bool _DisposedValue;

        /// <summary>Dispose override</summary>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _DisposedValue = true;
            if (disposing)
            {
                if (!_LeaveStreamOpen)
                {
                    _Stream.Dispose();
                }
            }
        }
    }
}
