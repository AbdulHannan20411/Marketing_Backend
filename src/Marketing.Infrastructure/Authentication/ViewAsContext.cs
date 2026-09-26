using Marketing.Shared.Abstractions;

namespace Marketing.Infrastructure.Authentication;

/// <summary>
/// Holds the previewed teammate for one request.
/// </summary>
/// <remarks>
/// Registered scoped and held in a field, for the same reason <c>TenantContext</c> is: an
/// <see cref="AsyncLocal{T}"/> written inside an <c>async</c> method is discarded when that method
/// returns, so a resolver that awaits a database read before entering the scope would hand its
/// caller a scope that was already gone - and a preview that silently stopped applying would show
/// an administrator their own data under a banner naming somebody else.
/// </remarks>
public sealed class ViewAsContext : IViewAsContext
{
    private ViewAsIdentity? _previewing;

    /// <inheritdoc />
    public ViewAsIdentity? Previewing => _previewing;

    /// <inheritdoc />
    public bool IsPreviewing => _previewing is not null;

    /// <inheritdoc />
    public IDisposable BeginScope(ViewAsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // One preview per request. Nesting would mean a second parameter somewhere deciding to
        // narrow further, and there is no such caller - if one ever appears it should be written
        // deliberately rather than inherited from this.
        if (_previewing is not null)
        {
            throw new InvalidOperationException("A view-as scope is already active for this request.");
        }

        _previewing = identity;

        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ViewAsContext _owner;
        private bool _disposed;

        public Scope(ViewAsContext owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._previewing = null;
        }
    }
}
