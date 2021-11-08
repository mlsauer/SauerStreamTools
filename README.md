# SauerStreamTools
Contains Tools for use with .Net Streams

#### DeferredCommitStream
Stream wrapper that stores all writes non persistently until it is explicitly commited.
- Useful as drop-in replacement when directly editing files with FileStream.
- Changes that are not explicitly commited will be discarded.
- Can be used with BinaryWriter, StreamReader, etc. like any normal Stream.
