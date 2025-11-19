using UnityEngine;

/// <summary>
/// Simple user manager for room identification.
/// Uses a simple identifier for the room/connection.
/// </summary>
public static class UserManager
{
    private static string _userName;

    public static string userName
    {
        get
        {
            if (string.IsNullOrEmpty(_userName))
            {
                // Generate a simple identifier based on device or random
                _userName = SystemInfo.deviceUniqueIdentifier;
                if (string.IsNullOrEmpty(_userName))
                {
                    _userName = "User_" + Random.Range(1000, 9999);
                }
            }
            return _userName;
        }
        set
        {
            _userName = value;
        }
    }
}

