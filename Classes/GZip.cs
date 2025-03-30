using System.IO.Compression;
using System.Text;

namespace Diyokee {
    public static class GZip {
        public static string Zip(this string text) {
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            using(MemoryStream msIn = new(buffer)) {
                using(MemoryStream msOut = new()) {
                    using(GZipStream gzStream = new(msOut, CompressionMode.Compress)) {
                        msIn.CopyTo(gzStream);
                    }
                    return Convert.ToBase64String(msOut.ToArray());
                }
            }
        }

        public static string UnZip(this string compressedText) {
            byte[] gzBuffer = Convert.FromBase64String(compressedText);
            using(MemoryStream msi = new(gzBuffer)) {
                using(MemoryStream mso = new()) {
                    using(GZipStream gs = new(msi, CompressionMode.Decompress)) {
                        gs.CopyTo(mso);
                    }

                    return Encoding.UTF8.GetString(mso.ToArray());
                }
            }
        }
    }
}
