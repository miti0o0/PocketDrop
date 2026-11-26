using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PocketDrop.Core
{
    public sealed class FolderChoice
    {
        /// <summary>"new" or "existing".</summary>
        [JsonPropertyName("mode")] public string Mode { get; set; } = "new";
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    public sealed class OfferedFile
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        /// <summary>Optional last-modified time, Unix epoch milliseconds.</summary>
        [JsonPropertyName("modified")] public long? Modified { get; set; }
    }

    public sealed class TransferOffer
    {
        [JsonPropertyName("device")] public string? Device { get; set; }
        [JsonPropertyName("folder")] public FolderChoice Folder { get; set; } = new();
        [JsonPropertyName("files")] public List<OfferedFile> Files { get; set; } = new();

        /// <summary>Set by the server, not by the phone: "Wi-Fi", "Bluetooth" or "Browser".</summary>
        [JsonIgnore] public string Transport { get; set; } = "";
    }

    public sealed class FolderInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("files")] public int Files { get; set; }
        [JsonPropertyName("modified")] public DateTime Modified { get; set; }
    }

    public enum RejectReason { Declined, Busy, Timeout, Invalid, NoSpace }

    public sealed class OfferResult
    {
        public bool Accepted => Session != null;
        public ReceiveSession? Session { get; init; }
        public RejectReason Reason { get; init; }

        public string ReasonCode => Reason switch
        {
            RejectReason.Busy => "busy",
            RejectReason.Timeout => "timeout",
            RejectReason.Invalid => "invalid",
            RejectReason.NoSpace => "no_space",
            _ => "declined",
        };
    }
}
