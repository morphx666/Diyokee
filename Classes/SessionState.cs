namespace Diyokee {
    public class SessionState {
        private Dictionary<string, object?> items { get; set; }

        public SessionState() {
            items = [];
        }

        public T? Get<T>(string key) => items.TryGetValue(key, out var obj) && obj is T value ? value : default;
        public void Set<T>(string key, T value) => items[key] = value;
    }
}
