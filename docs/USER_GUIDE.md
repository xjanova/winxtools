# NetX User Guide

## Table of Contents
1. [Getting Started](#getting-started)
2. [Dashboard](#dashboard)
3. [Network Monitor](#network-monitor)
4. [Bandwidth Control](#bandwidth-control)
5. [Uninstaller](#uninstaller)
6. [System Cleaner](#system-cleaner)
7. [Settings](#settings)
8. [FAQ](#faq)

---

## Getting Started

### Installation

**Portable Version (Recommended)**
1. Download NetX from the official source
2. Extract the ZIP file to any folder
3. Run `NetX.exe`

**Note**: NetX requires Administrator privileges for full functionality.

### First Launch

When you first run NetX:
1. Windows may ask for administrator permission - click **Yes**
2. Select your preferred language (English or Thai)
3. The Dashboard will appear showing your network activity

### System Tray

NetX can minimize to the system tray:
- Click the minimize button to minimize to tray
- Double-click the tray icon to restore
- Right-click the tray icon for quick options

---

## Dashboard

The Dashboard provides an overview of your system's network activity.

### Statistics Cards

| Card | Description |
|------|-------------|
| **Download Speed** | Current total download speed |
| **Upload Speed** | Current total upload speed |
| **Active Apps** | Number of apps using network |
| **Connections** | Total active network connections |

### Bandwidth Chart

The real-time chart shows:
- **Cyan line**: Download speed over time
- **Green line**: Upload speed over time

The chart updates every second and shows the last 60 data points.

### Top Consumers

Lists the top 5 applications using the most bandwidth:
- Application name
- Current speed
- Percentage of total bandwidth

### Quick Actions

| Button | Function |
|--------|----------|
| **Block All** | Block all network traffic (coming soon) |
| **Resume All** | Resume all blocked traffic (coming soon) |
| **Quick Clean** | Run quick system cleanup (coming soon) |

### Today's Statistics

Shows your total data usage for today:
- **Today Download**: Total data downloaded
- **Today Upload**: Total data uploaded

---

## Network Monitor

Detailed view of network activity per process.

### Process List

The main table shows:

| Column | Description |
|--------|-------------|
| **Process** | Application name and icon |
| **PID** | Process ID |
| **Download** | Current download speed |
| **Upload** | Current upload speed |
| **Connections** | Number of active connections |
| **Status** | Network status indicator |

### Filtering

**Search**: Type in the search box to filter by process name

**Sort**: Click column headers to sort

### Process Details

Select a process to see:
- Detailed connection information
- Per-process bandwidth chart
- Connection history

### Actions

| Button | Function |
|--------|----------|
| **Block** | Block this app's network access |
| **Limit** | Set bandwidth limit for this app |
| **Details** | Show detailed connection info |

---

## Bandwidth Control

Manage how applications use your network connection.

### Two Control Modes

#### Basic Mode (Windows Firewall)
- Simple block/allow functionality
- Uses Windows Firewall API
- Easy to use, no advanced configuration
- Perfect for most users

**How to use Basic Mode:**
1. Click "Basic Mode" card
2. Find the application in the list
3. Toggle the switch to block/allow

#### Advanced Mode (WFP - Windows Filtering Platform)
- Kernel-level network control
- Set specific bandwidth limits
- Priority-based traffic management
- For advanced users

**How to use Advanced Mode:**
1. Click "Advanced Mode" card
2. Click "Add Rule" button
3. Select an application
4. Set download/upload limits
5. Click "Save"

**Note**: Advanced Mode requires Administrator privileges

### Managing Rules

| Action | Description |
|--------|-------------|
| **Add Rule** | Create a new bandwidth rule |
| **Edit** | Modify existing rule |
| **Delete** | Remove a rule |
| **Enable/Disable** | Toggle rule without deleting |

### Rule Types

| Type | Description |
|------|-------------|
| **Block** | Completely block network access |
| **Limit** | Set maximum bandwidth |
| **Priority** | Set traffic priority level |

---

## Uninstaller

Remove applications completely from your system.

### Installed Programs List

Shows all installed applications with:

| Column | Description |
|--------|-------------|
| **Name** | Application name |
| **Publisher** | Software publisher |
| **Size** | Estimated disk space used |
| **Install Date** | When it was installed |

### Search & Filter

- **Search box**: Filter by application name
- **Show System Apps**: Toggle to show Windows system applications

### Uninstall Options

#### Standard Uninstall
Runs the application's built-in uninstaller.

1. Find the application
2. Click "Uninstall"
3. Confirm the action
4. Follow the uninstaller prompts

#### Deep Clean
Removes the application AND all leftover files.

1. Find the application
2. Click "Deep Clean"
3. Confirm the action
4. NetX will:
   - Run the standard uninstaller
   - Scan for leftover files in:
     - Program Files
     - AppData
     - ProgramData
   - Scan Registry for leftover keys
   - Show found leftovers for confirmation
   - Remove all selected leftovers

**Warning**: Deep Clean is more thorough but may take longer. Always review leftovers before deletion.

### Tips

- Sort by "Size" to find large applications
- Sort by "Install Date" to find old unused apps
- Use "Refresh" after uninstalling to update the list

---

## System Cleaner

Free up disk space by removing unnecessary files.

### Cleanup Categories

| Category | What it cleans |
|----------|----------------|
| **Temporary Files** | Windows temp folder contents |
| **Browser Cache** | Web browser cached data |
| **Windows Update** | Old Windows update files |
| **Recycle Bin** | Deleted files in recycle bin |
| **Thumbnails** | Cached image thumbnails |
| **Log Files** | System and application logs |
| **Old Windows** | Windows.old folder (previous installation) |

### How to Use

1. **Scan**: Click "Scan" to analyze what can be cleaned
2. **Review**: Check the sizes for each category
3. **Select**: Choose which categories to clean (checkboxes)
4. **Clean**: Click "Clean" to remove selected items

### Understanding the Results

After scanning, you'll see:
- **Total Space**: Combined size that can be freed
- **Files Found**: Number of files that will be deleted
- **Per-category sizes**: Size for each cleanup category

### Safety Notes

- Cleaning is **permanent** - files cannot be recovered
- Temporary files are generally safe to delete
- Browser cache will require re-downloading website assets
- Old Windows folder removal prevents rollback to previous version
- Important files are never touched

### Progress Indicator

During cleanup:
- Progress bar shows completion percentage
- Status text shows current operation
- Wait for "Cleanup Complete" message

---

## Settings

Configure NetX to your preferences.

### General Settings

#### Language
- **English**: UI in English
- **Thai (ไทย)**: UI in Thai

Changes apply immediately - no restart required.

#### Start with Windows
When enabled, NetX will automatically launch when Windows starts.

#### Minimize to Tray
When enabled, closing or minimizing the window keeps NetX running in the system tray.

#### Notifications
Enable/disable desktop notifications for:
- Network alerts
- Cleanup completion
- Update availability

### Network Settings

#### Refresh Rate
How often network statistics update:
- **500ms (Fast)**: Most responsive, higher CPU usage
- **1 second**: Balanced (recommended)
- **2 seconds**: Lower CPU usage
- **5 seconds**: Minimal CPU usage

#### Data Retention
How long to keep network history:
- 7 days
- 14 days
- 30 days (default)
- 90 days

Older data is automatically deleted.

### Bandwidth Control Settings

#### Default Control Mode
Which mode to use when adding new rules:
- **Basic Mode**: Windows Firewall-based
- **Advanced Mode (WFP)**: Kernel-level filtering

### About

Shows:
- Application version
- Credits
- Administrator status

### Actions

| Button | Function |
|--------|----------|
| **Check for Updates** | Check if a newer version is available |
| **Reset Settings** | Restore all settings to defaults |

---

## FAQ

### General Questions

**Q: Why does NetX need administrator privileges?**
A: NetX needs elevated access to:
- Monitor network connections at the system level
- Modify Windows Firewall rules
- Access certain registry keys
- Set process priorities

**Q: Is NetX safe to use?**
A: Yes. NetX does not:
- Send your data to external servers
- Modify critical system files
- Install additional software
- Contain ads or tracking

**Q: Can I run NetX without administrator?**
A: Yes, but with limited functionality:
- Network monitoring will work
- Bandwidth control will not work
- Some cleanup operations may fail

### Network Monitor

**Q: Why are the speeds shown as estimates?**
A: Windows doesn't provide per-process bandwidth directly. NetX estimates based on connection activity and system-wide statistics.

**Q: Why don't I see all processes?**
A: Only processes with active network connections are shown. Background processes without current network activity won't appear.

### Bandwidth Control

**Q: What's the difference between Basic and Advanced mode?**
A: Basic mode uses Windows Firewall (block/allow only). Advanced mode uses WFP for granular control (speed limits, priorities).

**Q: My rules aren't working, why?**
A: Ensure you:
- Are running as Administrator
- Have saved the rule
- The rule is enabled (toggle on)
- The process name is correct

### Cleaner

**Q: Is it safe to clean all categories?**
A: Generally yes, but:
- Browser cache: You'll need to re-download website data
- Old Windows: You won't be able to rollback to previous version
- Review what will be deleted before confirming

**Q: Can I recover deleted files?**
A: No. Cleaned files are permanently deleted, not moved to Recycle Bin.

### Uninstaller

**Q: What does Deep Clean remove?**
A: In addition to the standard uninstaller:
- Files in Program Files / Program Files (x86)
- Files in AppData (Local, Roaming, LocalLow)
- Files in ProgramData
- Registry entries related to the application

**Q: An application won't uninstall, what should I do?**
A: Try:
1. Close all instances of the application
2. Restart your computer
3. Try the uninstall again
4. Use Deep Clean option

### Performance

**Q: NetX is using too much CPU, what can I do?**
A: Go to Settings and:
- Increase refresh rate to 2 or 5 seconds
- Reduce data retention period
- Close Network Monitor when not needed

**Q: NetX is using too much memory?**
A: This is usually due to:
- Long-running session (restart NetX)
- Many tracked processes (normal behavior)
- Chart history (restart clears it)

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl + D` | Go to Dashboard |
| `Ctrl + N` | Go to Network Monitor |
| `Ctrl + B` | Go to Bandwidth Control |
| `Ctrl + U` | Go to Uninstaller |
| `Ctrl + L` | Go to Cleaner |
| `Ctrl + ,` | Go to Settings |
| `Ctrl + Q` | Quit application |
| `Escape` | Minimize to tray |

---

## Getting Help

If you need additional help:

1. **Check this guide** - Most questions are answered here
2. **Contact Support** - support@xmanstudio.com
3. **Report Issues** - GitHub Issues page

---

**Document Version**: 1.0
**Last Updated**: December 2024
**Author**: xman studio
