using Unity.WebRTC;
using System.Collections.Generic;

public static class WebRTCConfig
{
    public static readonly List<RTCIceServer> IceServers = new List<RTCIceServer>
    {
        new RTCIceServer
        {
            urls = new[]
            {
                "stun:stun.l.google.com:19302",
                "stun:stun1.l.google.com:19302",
                "stun:stun2.l.google.com:19302",
                "stun:stun3.l.google.com:19302",
                "stun:stun4.l.google.com:19302"
            }
        },
        new RTCIceServer
        {
            urls = new[]
            {
                "turn:openrelay.metered.ca:3478?transport=udp",
                "turn:openrelay.metered.ca:80?transport=udp",
                "turn:openrelay.metered.ca:5349?transport=udp",
                "turn:openrelay.metered.ca:443?transport=tcp",
                "turn:openrelay.metered.ca:80?transport=tcp",
                "turn:openrelay.metered.ca:3478?transport=tcp",
                "turn:openrelay.metered.ca:5349?transport=tcp"
            },
            username = "openrelayproject",
            credential = "openrelayproject"
        },
        new RTCIceServer
        {
            urls = new[]
            {
                "turn:turn.anyfirewall.com:3478?transport=udp",
                "turn:turn.anyfirewall.com:5349?transport=udp",
                "turn:turn.anyfirewall.com:443?transport=tcp",
                "turn:turn.anyfirewall.com:80?transport=tcp",
                "turn:turn.anyfirewall.com:3478?transport=tcp",
                "turn:turn.anyfirewall.com:5349?transport=tcp"
            },
            username = "webrtc",
            credential = "webrtc"
        }
    };
} 