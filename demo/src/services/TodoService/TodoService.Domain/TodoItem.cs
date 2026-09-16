using Noelia.Core.Identity;

namespace TodoService.Domain;

public sealed class TodoItem
{
    private TodoItem(Guid id, SubjectId owner, string title, DateTimeOffset createdAt)
    {
        Id = id;
        Owner = owner;
        Title = title;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    /// <summary>
    /// Whose todo this is. A subject, not a tenant: a todo belongs to a person
    /// and follows them, whichever company they might also act for.
    /// </summary>
    public SubjectId Owner { get; }

    public string Title { get; }
    public bool IsCompleted { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static TodoItem Create(SubjectId owner, string title, DateTimeOffset now)
    {
        if (owner.IsEmpty)
        {
            throw new ArgumentException("A todo must have an owner.", nameof(owner));
        }

        var normalisedTitle = title.Trim();
        if (normalisedTitle.Length is < 2 or > 160)
        {
            throw new ArgumentException("Todo title must contain between 2 and 160 characters.", nameof(title));
        }

        return new TodoItem(Guid.NewGuid(), owner, normalisedTitle, now);
    }

    public void Complete(DateTimeOffset now)
    {
        if (IsCompleted)
        {
            return;
        }

        IsCompleted = true;
        CompletedAt = now;
    }
}
