using System.Threading.Channels;

namespace TravelApi.Infrastructure.Logging
{
    public static class LogChannel
    {
        private static readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
        public static ChannelWriter<string> Writer => _channel.Writer;
        public static ChannelReader<string> Reader => _channel.Reader;
    }
}
