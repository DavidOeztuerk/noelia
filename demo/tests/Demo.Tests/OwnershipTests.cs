using Noelia.Core.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using TodoService.Application;
using TodoService.Infrastructure;

namespace Demo.Tests;

/// <summary>
/// A todo belongs to one person, and the boundary is the only place that says
/// which.
/// </summary>
public class OwnershipTests
{
    private readonly InMemoryTodoRepository _todos = new();
    private readonly TimeProvider _clock = TimeProvider.System;

    [Fact]
    public async Task A_list_holds_only_what_the_caller_owns()
    {
        var mine = SubjectId.New();
        var theirs = SubjectId.New();

        await Create(mine, "meine Aufgabe");
        await Create(theirs, "fremde Aufgabe");

        var listed = await new ListTodosQueryHandler(_todos)
            .Handle(new ListTodosQuery(mine), CancellationToken.None);

        listed.Should().ContainSingle().Which.Title.Should().Be("meine Aufgabe");
    }

    /// <summary>
    /// Completing someone else's todo answers exactly as completing one that
    /// does not exist.
    /// </summary>
    /// <remarks>
    /// Two different answers would let a caller learn which identifiers are in
    /// use by reading the difference.
    /// </remarks>
    [Fact]
    public async Task Someone_elses_todo_answers_like_a_missing_one()
    {
        var mine = SubjectId.New();
        var theirs = SubjectId.New();
        var theirTodo = await Create(theirs, "fremde Aufgabe");

        var handler = new CompleteTodoCommandHandler(
            _todos, _clock, NullLogger<CompleteTodoCommandHandler>.Instance);

        var foreign = await handler.Handle(new CompleteTodoCommand(mine, theirTodo.Id), CancellationToken.None);
        var missing = await handler.Handle(new CompleteTodoCommand(mine, Guid.NewGuid()), CancellationToken.None);

        foreign.Should().BeNull();
        missing.Should().BeNull();
    }

    [Fact]
    public async Task Completing_ones_own_todo_works()
    {
        var mine = SubjectId.New();
        var todo = await Create(mine, "meine Aufgabe");

        var completed = await new CompleteTodoCommandHandler(
                _todos, _clock, NullLogger<CompleteTodoCommandHandler>.Instance)
            .Handle(new CompleteTodoCommand(mine, todo.Id), CancellationToken.None);

        completed!.IsCompleted.Should().BeTrue();
    }

    /// <summary>
    /// A refused completion must leave the todo untouched, not merely answer
    /// with nothing.
    /// </summary>
    [Fact]
    public async Task A_refused_completion_changes_nothing()
    {
        var mine = SubjectId.New();
        var theirs = SubjectId.New();
        var theirTodo = await Create(theirs, "fremde Aufgabe");

        await new CompleteTodoCommandHandler(_todos, _clock, NullLogger<CompleteTodoCommandHandler>.Instance)
            .Handle(new CompleteTodoCommand(mine, theirTodo.Id), CancellationToken.None);

        var owners = await new ListTodosQueryHandler(_todos)
            .Handle(new ListTodosQuery(theirs), CancellationToken.None);

        owners.Should().ContainSingle().Which.IsCompleted.Should().BeFalse();
    }

    private Task<TodoService.Contracts.TodoResponse> Create(SubjectId owner, string title) =>
        new CreateTodoCommandHandler(_todos, _clock, NullLogger<CreateTodoCommandHandler>.Instance)
            .Handle(new CreateTodoCommand(owner, title), CancellationToken.None);
}
