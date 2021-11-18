using System.Diagnostics;
using System.IO;
using System.Text;

namespace Bannerlord.ReferenceAssemblies
{
    public class CustomTraceListener : TraceListener
    {
        private readonly TextWriter _sink;

        private readonly StringBuilder _buffer = new();

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private static readonly double StopwatchFrequencyFp = Stopwatch.Frequency;

        private string LinePrefix
        {
            get
            {
                var ticks = _stopwatch.ElapsedTicks;
                return $"[{ticks / StopwatchFrequencyFp:F7}] ";
            }
        }

        private void FlushCompleteLines()
        {
            var eol = BufferIndexOfEol();
            if (eol == -1) return;

            do
            {
                var line = _buffer.ToString(0, eol);
                _sink.WriteLine(LinePrefix + line);
                _buffer.Remove(0, eol + 1);

                eol = BufferIndexOfEol();
            } while (eol != -1);
        }

        public CustomTraceListener(TextWriter sink)
            => _sink = sink;

        public override void Write(string? message)
        {
            if (message == null) return;

            if (_buffer.Length == 0 && message.EndsWith('\n'))
            {
                _sink.WriteLine(LinePrefix + message[0..^1]);
                return;
            }

            _buffer.Append(message);
            FlushCompleteLines();
        }

        public override void WriteLine(string? message)
        {
            if (message == null) return;

            if (_buffer.Length == 0)
            {
                _sink.WriteLine(LinePrefix + message);
                return;
            }

            _buffer.AppendLine(message);
            FlushCompleteLines();
        }

        private int BufferIndexOfEol()
        {
            for (var i = 0; i < _buffer.Length; ++i)
            {
                if (_buffer[i] == '\n')
                    return i;
            }

            return -1;
        }

        protected override void Dispose(bool disposing)
        {
            FlushCompleteLines();
            _sink.WriteLine(LinePrefix + _buffer);
            _buffer.Clear();
        }
    }
}