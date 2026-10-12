using oaiResponsesAvalonia.Data;
using System;

namespace oaiResponsesAvalonia
{
    public class LogRowViewModel
    {
        public int SessionId { get; init; }
        public EndpointType Endpoint { get; init; }
        public string Title { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime LastUsedAt { get; init; }

        public int Turns { get; init; }
        public string Media { get; init; } = "—";
        public string Tools { get; init; } = "—";
        public string Model { get; init; } = "—";
        public string Dev { get; init; } = "No";

        public ChatSession Session { get; init; } = null!;
    }
}