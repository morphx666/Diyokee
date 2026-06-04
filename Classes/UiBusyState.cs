using System.Threading;

namespace Diyokee {
    public sealed class UiBusyState {
        private int counter;

        public bool IsBusy => counter > 0;

        public event Action? Changed;

        public IDisposable Begin() {
            Interlocked.Increment(ref counter);
            Changed?.Invoke();
            return new Scope(this);
        }

        private void End() {
            if(Interlocked.Decrement(ref counter) < 0) {
                counter = 0;
            }

            Changed?.Invoke();
        }

        private sealed class Scope(UiBusyState owner) : IDisposable {
            private bool disposed;

            public void Dispose() {
                if(disposed) return;
                disposed = true;
                owner.End();
            }
        }
    }
}
