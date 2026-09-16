using Noelia.Application.Extensions;
using Noelia.Core.Identity;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TodoService.Contracts;
using TodoService.Domain;

namespace TodoService.Application;

public interface ITodoRepository
{
    ValueTask AddAsync(TodoItem todo, CancellationToken cancellationToken);
    ValueTask<TodoItem?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<TodoItem>> ListOwnedByAsync(SubjectId owner, CancellationToken cancellationToken);
    ValueTask SaveAsync(TodoItem todo, CancellationToken cancellationToken);
}

/// <summary>
/// Every request names its owner, and the boundary is the only place that may
/// fill it in — from the verified token, never from the body.
/// </summary>
public sealed record CreateTodoCommand(SubjectId Owner, string Title) : IRequest<TodoResponse>;

public sealed record ListTodosQuery(SubjectId Owner) : IRequest<IReadOnlyList<TodoResponse>>;

public sealed record CompleteTodoCommand(SubjectId Owner, Guid TodoId) : IRequest<TodoResponse?>;

public sealed class CreateTodoCommandHandler(
    ITodoRepository todos,
    TimeProvider clock,
    ILogger<CreateTodoCommandHandler> logger) : IRequestHandler<CreateTodoCommand, TodoResponse>
{
    public async Task<TodoResponse> Handle(CreateTodoCommand command, CancellationToken cancellationToken)
    {
        var todo = TodoItem.Create(command.Owner, command.Title, clock.GetUtcNow());
        await todos.AddAsync(todo, cancellationToken);
        logger.LogInformation("Created todo {TodoId}", todo.Id);
        return TodoMappings.ToContract(todo);
    }
}

public sealed class ListTodosQueryHandler(ITodoRepository todos)
    : IRequestHandler<ListTodosQuery, IReadOnlyList<TodoResponse>>
{
    public async Task<IReadOnlyList<TodoResponse>> Handle(ListTodosQuery query, CancellationToken cancellationToken) =>
        (await todos.ListOwnedByAsync(query.Owner, cancellationToken)).Select(TodoMappings.ToContract).ToArray();
}

/// <summary>
/// Completing someone else's todo answers the same as completing one that does
/// not exist.
/// </summary>
/// <remarks>
/// Two answers would let a caller learn which identifiers are in use by reading
/// the difference between 403 and 404.
/// </remarks>
public sealed class CompleteTodoCommandHandler(
    ITodoRepository todos,
    TimeProvider clock,
    ILogger<CompleteTodoCommandHandler> logger) : IRequestHandler<CompleteTodoCommand, TodoResponse?>
{
    public async Task<TodoResponse?> Handle(CompleteTodoCommand command, CancellationToken cancellationToken)
    {
        var todo = await todos.FindAsync(command.TodoId, cancellationToken);
        if (todo is null || todo.Owner != command.Owner)
        {
            return null;
        }

        todo.Complete(clock.GetUtcNow());
        await todos.SaveAsync(todo, cancellationToken);
        logger.LogInformation("Completed todo {TodoId}", todo.Id);
        return TodoMappings.ToContract(todo);
    }
}

public static class TodoMappings
{
    public static TodoResponse ToContract(TodoItem todo) =>
        new(todo.Id, todo.Title, todo.IsCompleted, todo.CreatedAt, todo.CompletedAt);
}

public static class TodoApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddTodoApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddCQRS([typeof(TodoApplicationServiceCollectionExtensions).Assembly]);
        return services;
    }
}
