namespace Revue;

public record Reply(
    string Id,
    string Author,
    string Body,
    string Created
);

public record Comment(
    string Id,
    string File,
    int Line,
    string LineContent,
    string Base,
    string Head,
    string Side,
    string Body,
    string Author,
    string Created,
    bool Resolved,
    List<Reply>? Replies,
    string? Branch = null
);

public record CommentRequest(
    string? Id,
    string File,
    int Line,
    string LineContent,
    string Base,
    string Head,
    string Side,
    string Body,
    string? Author,
    bool Resolved,
    string? Branch = null
);

public record ReplyRequest(
    string Author,
    string Body
);

public record DiffFile(string File, string Patch, int Additions, int Deletions);

public record RefStatus(string Remote, string Branch, int Behind);
