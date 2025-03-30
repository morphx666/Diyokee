using System.Diagnostics;

namespace Diyokee {

    // https://getsongkey.com/tools/notation-converter
    // https://github.com/iammordaty/key-tools
    public class KeyTools {
        public enum Notations {
            CamelotKey,
            OpenKey,
            MusicKey,
            MusicKeyAlt,
            MusicKeyBeatport,
            MusicKeyEssentia
        }
        public Notations Notation { get; set; } = Notations.CamelotKey;

        private static readonly string[] camelot = [
            "1A", "1B", "2A", "2B", "3A", "3B", "4A", "4B", "5A", "5B", "6A", "6B",
            "7A", "7B", "8A", "8B", "9A", "9B", "10A", "10B", "11A", "11B", "12A", "12B",
        ];

        private static readonly string[] openKey = [
            "6M", "6D", "7M", "7D", "8M", "8D", "9M", "9D", "10M", "10D", "11M",
            "11D", "12M", "12D", "1M", "1D", "2M", "2D", "3M", "3D", "4M", "4D", "5M", "5D",
        ];

        private static readonly string[] musicKey = [
            "Abm", "B", "Ebm", "Gb", "Bbm", "Db", "Fm", "Ab", "Cm", "Eb", "Gm", "Bb",
            "Dm", "F", "Am", "C", "Em", "G", "Bm", "D", "Gbm", "A", "Dbm", "E",
        ];

        private static readonly string[] musicKeyAlt = [
            "G#m", "B", "D#m", "F#", "A#m", "C#", "Fm", "G#", "Cm", "D#", "Gm", "A#",
            "Dm", "F", "Am", "C", "Em", "G", "Bm", "D", "F#m", "A", "C#m", "E",
        ];

        private static readonly string[] musicKeyBeatport = [
            "G#m", "Bmaj", "Ebm", "Gb", "Bbm", "Db", "Fmin", "Ab", "Cmin", "Eb", "Gmin", "Bb",
            "Dmin", "Fmaj", "Amin", "Cmaj", "Emin", "Gmaj", "Bmin", "Dmaj", "F#m", "Amaj", "C#m", "Emaj",
        ];

        private static readonly string[] musicKeyEssentia = [
            "Ab minor", "B major", "Eb minor", "F# major", "Bb minor", "C# major", "F minor", "Ab major", "C minor", "Eb major", "G minor", "Bb major",
            "D minor", "F major", "A minor", "C major", "E minor", "G major", "B minor", "D major", "F# minor", "A major", "C# minor", "E major",
        ];

        private static readonly Dictionary<Notations, string[]> notationToKeysMap = new() {
            { Notations.CamelotKey, camelot },
            { Notations.OpenKey, openKey },
            { Notations.MusicKey, musicKey },
            { Notations.MusicKeyAlt, musicKeyAlt },
            { Notations.MusicKeyBeatport, musicKeyBeatport },
            { Notations.MusicKeyEssentia, musicKeyEssentia }
        };

        private static readonly Dictionary<Notations, string[]> notationToKeysMapNormalized = new() {
            { Notations.CamelotKey, camelot.Select(Normalize).ToArray() },
            { Notations.OpenKey, openKey.Select(Normalize).ToArray() },
            { Notations.MusicKey, musicKey.Select(Normalize).ToArray() },
            { Notations.MusicKeyAlt, musicKeyAlt.Select(Normalize).ToArray() },
            { Notations.MusicKeyBeatport, musicKeyBeatport.Select(Normalize).ToArray() },
            { Notations.MusicKeyEssentia, musicKeyEssentia.Select(Normalize).ToArray() }
        };

        private static readonly Dictionary<string, Notations> keyToNotationMap = [];

        private const int WHEEL_KEYS_NUM = 12;

        public KeyTools() {
            foreach(var notationKeys in notationToKeysMapNormalized) {
                foreach(var key in notationKeys.Value) {
                    keyToNotationMap[key] = notationKeys.Key;
                }
            }
        }

        public string ConvertTo(string key, Notations newNotation) {
            try {
                if(!IsValidKey(key)) return key;

                var keyIndex = GetKeyIndex(key);
                return notationToKeysMap[newNotation][keyIndex];
            } catch {
                Debugger.Break();
                return key;
            }
        }

        public string CalculateKey(string key, int step = 0, bool toggleScale = false) {
            var keyIndex = GetKeyIndex(key);

            var notation = GetNotation(key);
            //var newKeyIndex = (keyIndex + step) % notationToKeysMap[notation].Length;
            var newKeyIndex = CalculateNewKeyIndex(keyIndex, step, toggleScale);

            return notationToKeysMap[notation][newKeyIndex];
        }

        private bool IsValidKey(string key) {
            if(string.IsNullOrEmpty(key)) return false;
            var keyIndex = GetKeyIndex(key);
            return IsValidKeyIndex(keyIndex);
        }

        private bool IsValidKeyIndex(int keyIndex) {
            return keyIndex >= 0 && keyIndex < WHEEL_KEYS_NUM * 2;
        }

        private int CalculateNewKeyIndex(int keyIndex, int step, bool toggleScale) {
            var currentKeyIndex = keyIndex;
            if(toggleScale) currentKeyIndex = currentKeyIndex % 2 == 0 ? currentKeyIndex + 1 : currentKeyIndex - 1;
            var stepChange = step > 0 ? step * 2 : WHEEL_KEYS_NUM * 2 + step * 2;

            return (stepChange + currentKeyIndex) % (WHEEL_KEYS_NUM * 2);
        }

        private int GetKeyIndex(string key) {
            string normalizedKey = Normalize(key);

            Notations notation = GetNotation(normalizedKey);
            return Array.IndexOf(notationToKeysMapNormalized[notation], normalizedKey);
        }

        private Notations GetNotation(string key) {
            return keyToNotationMap[Normalize(key)];
        }

        private static string Normalize(string key) {
            return key.ToLower().TrimStart('0');
        }
    }
}
