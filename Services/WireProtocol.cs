namespace p2p.Services;

public enum FrameType : byte
{
    Message = 1,
    FileRequest = 2,
    FileAccept = 3,
    FileDecline = 4,
    FileChunk = 5,
    FileComplete = 6,
    FileCancel = 7,
    Ping = 8
}

public class HelloFrame
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SigningPublicKey { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string EphemeralPublicKey { get; set; } = "";
    public string Signature { get; set; } = "";
}

public class WireMessage
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string FileSha256 { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
}

public class WireFileRequest
{
    public string TransferId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public class WireFileAccept
{
    public string TransferId { get; set; } = "";
}

public class WireFileDecline
{
    public string TransferId { get; set; } = "";
}

public class WireFileComplete
{
    public string TransferId { get; set; } = "";
    public bool Ok { get; set; }
}

public class WireFileCancel
{
    public string TransferId { get; set; } = "";
}
