namespace Diyokee {
    public static class Extensions {
        public static (T, int)[] ToArrayWithIndex<T>(this T[] list) {
            int i = 0;
            (T, int)[] result = new (T, int)[list.Length];
            foreach(T e in list) result[i] = (e, i++);
            return result;
        }
    }
}
