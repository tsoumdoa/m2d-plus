namespace M2DPlus.Core;

/// <summary>The cache owns one reference. Retirement forbids new leases; existing leases remain valid.</summary>
public sealed class LeaseEntry<T> : IDisposable where T : class, IDisposable
{
    private readonly object _sync = new();
    private T? _value;
    private int _leases;
    private bool _retired;
    public LeaseEntry(T value) => _value = value ?? throw new ArgumentNullException(nameof(value));
    public Lease Acquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            _leases++;
            return new(this, _value!);
        }
    }
    public void Dispose()
    {
        T? release = null;
        lock (_sync)
        {
            _retired = true;
            if (_leases == 0) { release = _value; _value = null; }
        }
        release?.Dispose();
    }
    private void Release()
    {
        T? release = null;
        lock (_sync)
        {
            if (--_leases == 0 && _retired) { release = _value; _value = null; }
        }
        release?.Dispose();
    }
    public sealed class Lease : IDisposable
    {
        private LeaseEntry<T>? _owner;
        private readonly T _value;
        internal Lease(LeaseEntry<T> owner, T value) { _owner = owner; _value = value; }
        public T Value => _owner is null ? throw new ObjectDisposedException(nameof(Lease)) : _value;
        /// <summary>Another lease on the same value, valid even after the cache retired the entry (this lease keeps the
        /// value alive while it is shared). Lets an owner hand a retained value to a job without asking the cache again.</summary>
        public Lease Share()
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(Lease));
            lock (owner._sync)
            {
                if (owner._leases == 0) throw new ObjectDisposedException(nameof(Lease));
                owner._leases++;
                return new(owner, _value);
            }
        }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
