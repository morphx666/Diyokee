using Microsoft.AspNetCore.DataProtection;

namespace Diyokee {
    // Encrypts and decrypts small secrets (currently Dropbox refresh tokens) so they
    // can be persisted in settings.json without being stored in plaintext. Backed by
    // the ASP.NET Core Data Protection API, whose keys are scoped to this machine.
    public static class Secrets {
        private static IDataProtector? protector;

        public static void Initialize(IDataProtectionProvider provider) {
            protector = provider.CreateProtector("Diyokee.Dropbox.v1");
        }

        // Returns the encrypted form of the given plaintext, or the value unchanged
        // when protection is unavailable or the value is empty.
        public static string Protect(string plaintext) {
            if(protector == null || string.IsNullOrEmpty(plaintext)) return plaintext;
            return protector.Protect(plaintext);
        }

        // Returns the decrypted form of the given ciphertext, or an empty string when
        // the value cannot be decrypted (e.g. produced on a different machine).
        public static string Unprotect(string ciphertext) {
            if(protector == null || string.IsNullOrEmpty(ciphertext)) return ciphertext;
            try {
                return protector.Unprotect(ciphertext);
            } catch {
                return "";
            }
        }
    }
}
