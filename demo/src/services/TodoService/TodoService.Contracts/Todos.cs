namespace TodoService.Contracts;

/// <summary>
/// A new todo carries no owner: the server reads that from the token, so a
/// caller cannot file work under someone else's name by editing a field.
/// </summary>
public sealed record CreateTodoRequest(string Title);

public sealed record TodoResponse(
    Guid Id,
    string Title,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
