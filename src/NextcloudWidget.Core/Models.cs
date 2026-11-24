namespace NextcloudWidget.Core;

public sealed record TaskItem(string Id, string Title, string ListName, string Description,
    DateTimeOffset? Due, bool Completed, bool CanWrite, string SyncState, string ListId = "");

public sealed record TaskList(string Id, string Name, bool CanWrite);

public sealed record MailItem(string Id, string Subject, string Sender, DateTimeOffset Date, string Body, bool Unread);

public sealed record SyncInfo(DateTimeOffset? TasksLastSync, DateTimeOffset? MailLastSync);
