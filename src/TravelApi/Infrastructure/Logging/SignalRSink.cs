using Serilog.Core;
using Serilog.Events;
using System.IO;

namespace TravelApi.Infrastructure.Logging
{
    public class SignalRSink : ILogEventSink
    {
        private readonly IFormatProvider? _formatProvider;

        public SignalRSink(IFormatProvider? formatProvider = null)
        {
            _formatProvider = formatProvider;
        }

        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage(_formatProvider);
            var timestamp = logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            var level = logEvent.Level.ToString().ToUpper();
            
            var formatted = $"[API] [{timestamp} {level}] {message}";
            
            if (logEvent.Exception != null)
            {
                formatted += $"\n{logEvent.Exception}";
            }

            LogChannel.Writer.TryWrite(formatted);
        }
    }
}
