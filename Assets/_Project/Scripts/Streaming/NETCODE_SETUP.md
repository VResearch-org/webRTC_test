# Netcode WebRTC Setup Guide

This guide explains how to set up your sender and receiver scenes to use Netcode for GameObjects instead of Firebase for WebRTC signaling.

## Overview

The system now uses Unity Netcode for GameObjects for the initial ICE exchange and connection signaling. One device acts as the **Host (Server/Receiver)** and the other as the **Client (Sender)**.

## Prerequisites

1. **Unity Netcode for GameObjects** package must be installed
   - Open Package Manager
   - Add package: `com.unity.netcode.gameobjects`

2. **Unity WebRTC** package (already in use)

## Scene Setup

### Sender Scene (Client)

1. **Create NetworkManager GameObject:**
   - Create empty GameObject named "NetworkManager"
   - Add `NetworkManager` component
   - Configure:
     - Transport: Use Unity Transport (default)
     - Run In Background: ✓ (checked)
     - Don't Destroy On Load: ✓ (checked)

2. **Create Signaling Prefab:**
   - Create empty GameObject named "WebRTCSignaling"
   - Add `NetworkObject` component
   - Add `NetcodeWebRTCSignaling` component
   - Make it a Prefab (drag to Project window)
   - **Important**: Keep the prefab, but don't place it in the scene

3. **Add Signaling Spawner:**
   - On NetworkManager GameObject, add `NetcodeSignalingSpawner` component
   - Assign the "WebRTCSignaling" prefab to the `Signaling Prefab` field

4. **Add Auto-Connect Script:**
   - On NetworkManager GameObject, add `SimpleNetcodeAutoConnect` component
   - Set `Start As Host` to **false** (Client mode)
   - Set `Auto Connect On Start` to **true**

5. **Your Existing Components:**
   - Keep your `SimpleMediaStreamSender` component as is
   - It will automatically wait for Netcode to be ready

### Receiver Scene (Host/Server)

1. **Create NetworkManager GameObject:**
   - Create empty GameObject named "NetworkManager"
   - Add `NetworkManager` component
   - Configure:
     - Transport: Use Unity Transport (default)
     - Run In Background: ✓ (checked)
     - Don't Destroy On Load: ✓ (checked)

2. **Create Signaling Prefab:**
   - Create empty GameObject named "WebRTCSignaling"
   - Add `NetworkObject` component
   - Add `NetcodeWebRTCSignaling` component
   - Make it a Prefab (drag to Project window)
   - **Important**: Keep the prefab, but don't place it in the scene

3. **Add Signaling Spawner:**
   - On NetworkManager GameObject, add `NetcodeSignalingSpawner` component
   - Assign the "WebRTCSignaling" prefab to the `Signaling Prefab` field

4. **Add Auto-Connect Script:**
   - On NetworkManager GameObject, add `SimpleNetcodeAutoConnect` component
   - Set `Start As Host` to **true** (Host/Server mode)
   - Set `Auto Connect On Start` to **true**

5. **Your Existing Components:**
   - Keep your `SimpleMediaStreamReceiver` component as is
   - Call `Init()` when ready (or set it up to auto-initialize)

## Connection Flow

1. **Receiver starts first** (Host/Server):
   - Starts as Host
   - Waits for Client to connect
   - Ready to receive WebRTC offer

2. **Sender starts** (Client):
   - Starts as Client
   - Connects to Host
   - Sends WebRTC offer once connected

3. **Automatic WebRTC Connection:**
   - Once Netcode connection is established, WebRTC signaling happens automatically
   - ICE candidates are exchanged
   - Video stream is established

## Important Notes

- **Order matters**: Start the Receiver (Host) scene first, then the Sender (Client) scene
- **Same Network**: Both devices must be on the same local network (or use Unity Relay/Cloud if needed)
- **Port**: Default Unity Transport uses port 7777. Make sure firewall allows this.
- **Auto-Connect**: The `SimpleNetcodeAutoConnect` script handles automatic connection. You can disable it and use the GUI buttons if needed.

## Troubleshooting

1. **Connection fails:**
   - Check both devices are on the same network
   - Check firewall settings
   - Verify NetworkManager is configured correctly

2. **WebRTC doesn't connect:**
   - Check that `NetcodeWebRTCSignaling` component is spawned as NetworkObject
   - Verify both scenes have the signaling component
   - Check console for errors

3. **Video not showing:**
   - Verify WebRTC configuration (STUN/TURN servers)
   - Check that RenderTexture is assigned correctly
   - Verify RawImage is assigned in receiver

## Manual Connection (Optional)

If you want to connect manually instead of auto-connect:

1. Disable `Auto Connect On Start` in `SimpleNetcodeAutoConnect`
2. Use the GUI buttons that appear at runtime
3. Or call `StartHost()` / `StartClient()` from your own code

## Network Transport Configuration

For local network testing, Unity Transport works out of the box. For internet connections, you may need to:
- Use Unity Relay Service
- Configure port forwarding
- Use a dedicated server

The current setup is optimized for **local network** (same WiFi/LAN) connections between two devices.

