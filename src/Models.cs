namespace Revue;

public record Comment(
    string Id,
    string File,
    int Line,
    string LineContent,
    string Base,
    string Head,
    string Side,
    string Body,
    string Created,
    bool Resolved
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
    bool Resolved
);

public record DiffFile(string File, string Patch, int Additions, int Deletions);
