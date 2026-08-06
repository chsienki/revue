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

public record DiffFile(string File, string Patch, int Additions, int Deletions, CommitMeta? Commit = null, PrMeta? Pr = null);

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

/// <summary>
/// The draft PR description for a repo: Copilot's answer to "what would you
/// write up as the PR body right now?". Base/Head/Branch record the review
/// context it was written against so a later mismatch can be flagged rather
/// than silently shown.
/// </summary>
public record PrDraft(
    string Title,
    string Body,
    string? Base,
    string? Head,
    string? Branch,
    string? Generated,
    string? GeneratedBy
);

// Cosmetic metadata attached to DiffFile for the PR-description entry. The body
// is already encoded into the patch lines, as with CommitMeta.
public record PrMeta(string Title, string? Generated, string? GeneratedBy, bool Stale);

// Body of PUT /api/pr.
public record PrDraftRequest(string? Title, string? Body);

// A request from the browser asking an attached Copilot session to address a set
// of comments. Status moves pending -> working (a waiter claimed it) -> done.
public record AgentRequest(
    string Id,
    string Repo,
    string? Branch,
    string Note,
    List<string> CommentIds,
    string Created,
    string Status,
    string? Claimed,
    string? Completed,
    string? Summary,
    string Kind = "comments",
    string? Base = null,
    string? Head = null
);

// Body of POST /api/agent/requests. A null CommentIds means "whatever is
// unresolved right now", resolved server-side at enqueue time. Kind selects the
// job: addressing comments, or redrafting the PR description.
public record AgentRequestBody(string? Note, List<string>? CommentIds, string? Kind);

// Body of POST /api/agent/requests/{id}/complete.
public record AgentCompleteBody(string? Summary);

// Snapshot of a repo's agent queue for the UI poll.
public record AgentStatusInfo(
    bool Attached,
    int Waiters,
    int Pending,
    AgentRequest? Queued,
    AgentRequest? Active,
    AgentRequest? Last
);
