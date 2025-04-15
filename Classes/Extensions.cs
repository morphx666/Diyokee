namespace Diyokee {
    public static class Extensions {
        public static (T, int)[] WithIndex<T>(this T[] array) {
            int i = 0;
            (T, int)[] result = new (T, int)[array.Length];
            foreach(T e in array) result[i] = (e, i++);
            return result;
        }

        public static List<(T, int)> WithIndex<T>(this List<T> list) {
            return [.. list.ToArray().WithIndex()];

            //int i = 0;
            //List<(T, int)> result = [];
            //foreach(T e in list) result[i] = (e, i++);
            //return result;
        }
    }
}