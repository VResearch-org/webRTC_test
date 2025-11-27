# WebRTC Streaming Demo - Quick Start Guide

## Setup

**You need two devices/apps:**
- **Sender (Headset)**: The device that streams video
- **Receiver (Tablet)**: The device that receives and displays the stream

## Connection Steps

### Option 1: LAN Connection (Same Network)

1. **On both Sender and Receiver**: Click **"Start with LAN"** button
2. Wait a few seconds for connection to establish
3. Video stream should appear on the receiver

### Option 2: Relay Connection (Internet)

1. **On Sender**: Click **"Start with Relay"** button
   - A join code will be generated (displayed on screen)
2. **On Receiver**: 
   - Enter the join code from the sender
   - Click **"Start with Relay"** button
3. Wait a few seconds for connection to establish
4. Video stream should appear on the receiver

## Using Video Controls

**⚠️ Important**: Video player controls only work **after connection is established**. Wait until you see the video stream before using these buttons.

- **Play/Pause Button**: Toggles video playback (only works when connected)
- **Skip Back Button**: Resets video to the beginning
- **Resolution Dropdown**: Change streaming quality (SD, HD, Full HD, QHD)

## Limitations

- **No restart button**: To restart, close and reopen both apps
- **Protocol switching**: To switch between LAN and Relay, you must restart **both** apps
- **Connection required**: Video player controls require an active connection

## Troubleshooting

- If connection fails, restart both apps and try again
- For LAN: Make sure both devices are on the same network
- For Relay: Double-check the join code is entered correctly

