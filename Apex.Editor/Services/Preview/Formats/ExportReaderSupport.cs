using System;
using System.IO;
using CallOfFile;

namespace Apex.Editor.Services.Preview.Formats;

/// <summary>
/// Opens a CallOfFile <see cref="TokenReader"/> for a model/anim file, working around a bug in the
/// vendored <see cref="ExportTokenReader"/>: it mishandles bare-LF and mixed line endings (which BO3
/// export files use), merging tokens across lines. Text (<c>*_export</c>) files are normalised to CRLF
/// while they are streamed in; binary (<c>*_bin</c>) files are passed straight through.
/// </summary>
internal static class ExportReaderSupport
{
    public static TokenReader? OpenReader(string path)
    {
        var ext = Path.GetExtension(path);
        var isExport =
            ext.Equals(".xmodel_export", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xanim_export", StringComparison.OrdinalIgnoreCase);

        if (isExport)
            return new ExportTokenReader(new CrlfStream(File.OpenRead(path)));

        return TokenReader.CreateReader(path);
    }

    /// <summary>
    /// Read-only view of a text stream with every line ending (CRLF, lone LF, lone CR) written as CRLF — byte for byte
    /// what replacing "\r\n" and "\r" by "\n" and then "\n" by "\r\n" in the decoded text produces.
    /// </summary>
    private sealed class CrlfStream(Stream inner) : Stream
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _pos, _len;
        private bool _pendingLf, _afterCr;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            int n = 0;
            if (_pendingLf && n < destination.Length)
            {
                destination[n++] = (byte)'\n';
                _pendingLf = false;
            }
            while (n < destination.Length)
            {
                if (_pos == _len)
                {
                    _len = inner.Read(_buffer, 0, _buffer.Length);
                    _pos = 0;
                    if (_len == 0)
                        break;
                }
                byte b = _buffer[_pos++];
                if (b == (byte)'\n' && _afterCr)
                {
                    _afterCr = false; // the LF of a CRLF, already written
                    continue;
                }
                _afterCr = b == (byte)'\r';
                if (b is (byte)'\n' or (byte)'\r')
                {
                    destination[n++] = (byte)'\r';
                    if (n < destination.Length)
                        destination[n++] = (byte)'\n';
                    else
                        _pendingLf = true;
                }
                else
                {
                    destination[n++] = b;
                }
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
