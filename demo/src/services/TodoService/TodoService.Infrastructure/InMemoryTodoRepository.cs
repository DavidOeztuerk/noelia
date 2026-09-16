using System.Collections.Concurrent;
using Noelia.Core.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TodoService.Application;
using TodoService.Domain;

namespace TodoService.Infrastructure;

/// <summary>
/// Keeps todos in this process. Everything is lost on restart, which is what
/// makes this a demo and not a deployment.
/// </summary>
public sealed class InMemoryTodoRepository : ITodoRepository
{
    private readonly ConcurrentDictionary<Guid, TodoItem> _todos = new();

    public ValueTask AddAsync(TodoItem todo, CancellationToken cancellationToken)
    {
        _todos[todo.Id] = todo;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TodoItem?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_todos.GetValueOrDefault(id));

    public Task<IReadOnlyList<TodoItem>> ListOwnedByAsync(SubjectId owner, CancellationToken cancellationToken)
    {
        var todos = _todos.Values
            .Where(todo => todo.Owner == owner)
            .OrderByDescending(todo => todo.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<TodoItem>>(todos);
    }

    /// <summary>
    /// Writes the aggregate back.
    /// </summary>
    /// <remarks>
    /// A no-op against this store, because the entry is the same object the
    /// caller mutated. It exists so that a store where that is not true — any
    /// database — needs no change to the handlers.
    /// </remarks>
    public ValueTask SaveAsync(TodoItem todo, CancellationToken cancellationToken)
    {
        _todos[todo.Id] = todo;
        return ValueTask.CompletedTask;
    }
}

public static class TodoInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddTodoInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ITodoRepository, InMemoryTodoRepository>();

        // An explicit statement rather than an empty list. This service holds
        // its todos in the process and reaches nothing else, so readiness is
        // the same question as liveness — but saying so is not the same as
        // saying nothing. An empty /health/ready answers 200 whether the
        // service depends on nothing or whether somebody forgot to register
        // the dependency it does have, and an orchestrator cannot tell those
        // apart.
        services.AddHealthChecks()
            .AddCheck(
                "todo-store",
                () => HealthCheckResult.Healthy("in-process store; this service reaches nothing else"),
                tags: ["ready"]);

        return services;
    }
}
