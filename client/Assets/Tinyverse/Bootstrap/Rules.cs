using P = Tinyverse.Proto;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// The server's permission rules (SPEC 6, tv-world <c>objects.rs</c>), so the client can grey out
    /// what the server would refuse. The server stays authoritative.
    /// </summary>
    public static class Permissions
    {
        /// <summary>Builders and owners may create objects.</summary>
        public static bool CanBuild(P.Role role) => role == P.Role.Builder || role == P.Role.Owner;

        /// <summary>
        /// Edit or delete: owners always; builders their own objects and unlocked objects of others;
        /// visitors never.
        /// </summary>
        public static bool CanEdit(P.Role role, string userId, string objectOwnerId, bool locked)
        {
            switch (role)
            {
                case P.Role.Owner: return true;
                case P.Role.Builder: return IsOwn(userId, objectOwnerId) || !locked;
                default: return false;
            }
        }

        /// <summary>Changing the locked flag: world owners, and the object's owner if they may edit at all.</summary>
        public static bool CanLock(P.Role role, string userId, string objectOwnerId) =>
            role == P.Role.Owner || (role == P.Role.Builder && IsOwn(userId, objectOwnerId));

        static bool IsOwn(string userId, string ownerId) => !string.IsNullOrEmpty(userId) && userId == ownerId;
    }

    /// <summary>Sentences for the person, one per <see cref="P.ErrorCode"/>.</summary>
    public static class ErrorText
    {
        public static string Describe(P.ErrorCode code)
        {
            switch (code)
            {
                case P.ErrorCode.BadRequest: return "The server did not understand that request.";
                case P.ErrorCode.UnsupportedVersion: return "This client is too old or too new for this world. Update Tinyverse.";
                case P.ErrorCode.AuthFailed: return "Wrong or missing password for this world.";
                case P.ErrorCode.InvalidToken: return "Your saved login for this world is no longer valid.";
                case P.ErrorCode.Banned: return "You are banned from this world.";
                case P.ErrorCode.WorldFull: return "This world is full. Try again later.";
                case P.ErrorCode.PermissionDenied: return "You are not allowed to do that here.";
                case P.ErrorCode.NotFound: return "That no longer exists.";
                case P.ErrorCode.InvalidArgument: return "That value is not allowed.";
                case P.ErrorCode.OutOfBounds: return "That is outside the world's bounds.";
                case P.ErrorCode.LimitExceeded: return "The world has reached a limit.";
                case P.ErrorCode.RateLimited: return "Slow down a little: too many requests.";
                case P.ErrorCode.Internal: return "The server had a problem with that request.";
                default: return "The server refused the request.";
            }
        }

        /// <summary>The sentence for the code, followed by the server's own explanation when it adds something.</summary>
        public static string Describe(P.ErrorCode code, string serverMessage)
        {
            string s = Describe(code);
            if (string.IsNullOrWhiteSpace(serverMessage)) return s;
            return s + " (" + serverMessage.Trim() + ")";
        }
    }
}
