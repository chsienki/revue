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

public record DiffFile(string File, string Patch, int Additions, int Deletions, CommitMeta? Commit = null);

// A repository served by this instance, as exposed to the frontend. Name is a
// minimal unique label (usually the last path segment) computed by RepoRegistry.
public record RepoInfo(string Path, string Name, string DefaultBase);

// Body of POST /api/repos, used by the single-instance hand-off to register a
// repo launched from another terminal into the already-running instance.
public record AddRepoRequest(string Path);

public record RefStatus(string Remote, string Branch, int Behind);

public record CommitInfo(string Hash, string Subject, string Body, string Author, string Date);

// Slim metadata attached to DiffFile when the file represents a commit message.
// Body is intentionally omitted -- it's already encoded into the patch lines.
public record CommitMeta(string Hash, string Subject, string Author, string Date);
