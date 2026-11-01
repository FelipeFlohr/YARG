using System.Security.Cryptography;
using System.Text;

namespace YARG.Relay
{
    /// <summary>
    /// A room password as a salted PBKDF2 hash, so the plain text is never kept.
    /// </summary>
    public sealed class PasswordHasher
    {
        private const int SALT_BYTES = 16;
        private const int HASH_BYTES = 32;

        private readonly byte[] _salt;
        private readonly byte[] _hash;
        private readonly int    _iterations;

        public PasswordHasher(string password, int iterations)
        {
            _iterations = iterations;
            _salt = RandomNumberGenerator.GetBytes(SALT_BYTES);
            _hash = Derive(password, _salt, iterations);
        }

        public bool Verify(string password)
        {
            // Constant time, so the time taken says nothing about how much of the password matched
            return CryptographicOperations.FixedTimeEquals(Derive(password, _salt, _iterations), _hash);
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                HashAlgorithmName.SHA256, HASH_BYTES);
        }
    }
}
