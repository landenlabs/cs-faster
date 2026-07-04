// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;

namespace Faster
{
    /// <summary>Rough grouping used to answer "can I safely ignore/disable this?" at a glance.</summary>
    public enum ServiceCategory
    {
        /// <summary>Needed for basic OS/session operation - don't disable.</summary>
        Core,
        /// <summary>Antivirus, firewall, auth, encryption, UAC.</summary>
        Security,
        /// <summary>Networking (discovery, connectivity, sharing, VPN).</summary>
        Network,
        /// <summary>Disks, volumes, backup/shadow-copy, file sharing plumbing.</summary>
        Storage,
        /// <summary>Drivers/peripherals-facing services (audio, print, Bluetooth, sensors, telephony).</summary>
        Hardware,
        /// <summary>Shell/desktop cosmetics and input.</summary>
        UI,
        /// <summary>Scheduled/maintenance work (updates, diagnostics reporting, cleanup, licensing).</summary>
        Background,
        /// <summary>Doesn't affect core OS function - telemetry, search indexing, assistants,
        /// language packs, consumer features. The usual candidates for a "trim for performance" list.</summary>
        Auxiliary,
        /// <summary>Not in this catalog - usually a third-party/OEM/driver-bundled service.</summary>
        Unknown,
    }

    public sealed class ServiceCatalogEntry
    {
        public ServiceCategory Category { get; init; } = ServiceCategory.Unknown;
        public string Purpose { get; init; } = "";
    }

    /// <summary>
    /// A curated, best-effort reference of well-known Windows service names (the short
    /// <c>ServiceName</c>, not the localized display name) to a rough category and one-line
    /// purpose. This is community/documentation knowledge baked in at build time, not queried
    /// live from anywhere - it will be incomplete (especially for third-party/OEM services,
    /// which correctly fall back to <see cref="ServiceCategory.Unknown"/>) and the odd entry may
    /// be wrong or drift as Windows changes. Treat it as a helpful starting point, not a
    /// guarantee, before disabling anything it flags.
    /// </summary>
    public static class ServiceCatalog
    {
        public static string CategoryLabel(ServiceCategory c) => c switch
        {
            ServiceCategory.Core => "Core",
            ServiceCategory.Security => "Security",
            ServiceCategory.Network => "Network",
            ServiceCategory.Storage => "Storage",
            ServiceCategory.Hardware => "Hardware",
            ServiceCategory.UI => "UI/Shell",
            ServiceCategory.Background => "Background",
            ServiceCategory.Auxiliary => "Auxiliary",
            _ => "Unknown",
        };

        private static readonly ServiceCatalogEntry Unclassified = new()
        {
            Category = ServiceCategory.Unknown,
            Purpose = "Not in Faster's built-in catalog, so there's no cached description. " +
                      "Commonly a third-party, OEM, or driver-bundled service - check its binary " +
                      "path (Details' WMI lookup) or search the web for what installed it.",
        };

        public static ServiceCatalogEntry GetOrUnknown(string serviceName) =>
            Entries.TryGetValue(serviceName, out var e) ? e : Unclassified;

        private static ServiceCatalogEntry Core(string purpose) => new() { Category = ServiceCategory.Core, Purpose = purpose };
        private static ServiceCatalogEntry Security(string purpose) => new() { Category = ServiceCategory.Security, Purpose = purpose };
        private static ServiceCatalogEntry Network(string purpose) => new() { Category = ServiceCategory.Network, Purpose = purpose };
        private static ServiceCatalogEntry Storage(string purpose) => new() { Category = ServiceCategory.Storage, Purpose = purpose };
        private static ServiceCatalogEntry Hardware(string purpose) => new() { Category = ServiceCategory.Hardware, Purpose = purpose };
        private static ServiceCatalogEntry UI(string purpose) => new() { Category = ServiceCategory.UI, Purpose = purpose };
        private static ServiceCatalogEntry Background(string purpose) => new() { Category = ServiceCategory.Background, Purpose = purpose };
        private static ServiceCatalogEntry Auxiliary(string purpose) => new() { Category = ServiceCategory.Auxiliary, Purpose = purpose };

        private static readonly Dictionary<string, ServiceCatalogEntry> Entries =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- Core: basic OS / session plumbing -------------------------------------- //
            ["RpcSs"] = Core("Remote Procedure Call - the inter-process communication most of Windows depends on."),
            ["DcomLaunch"] = Core("Starts COM/DCOM server processes on demand for other services and apps."),
            ["LSM"] = Core("Local Session Manager - tracks user sessions (console, RDP)."),
            ["EventLog"] = Core("Windows Event Log - records system/application/security events."),
            ["PlugPlay"] = Core("Detects and configures hardware as it's attached/removed."),
            ["Power"] = Core("Manages power policy and sleep/hibernate transitions."),
            ["ProfSvc"] = Core("Loads/unloads user profiles at logon/logoff."),
            ["SamSs"] = Core("Security Accounts Manager - local account database used at logon."),
            ["Schedule"] = Core("Task Scheduler - runs scheduled tasks (many OS maintenance tasks depend on it)."),
            ["Winmgmt"] = Core("Windows Management Instrumentation - the WMI backend many tools/scripts query."),
            ["CryptSvc"] = Core("Cryptographic Services - catalog/signature verification, cert store updates."),
            ["gpsvc"] = Core("Group Policy Client - applies local/domain policy settings."),
            ["SENS"] = Core("System Event Notification Service - notifies apps of logon/network/power events."),
            ["W32Time"] = Core("Windows Time - keeps the system clock synchronized; auth/certs rely on accurate time."),
            ["AppReadiness"] = Core("Prepares a user's environment the first time a Store app is used."),
            ["Appinfo"] = Core("Application Information - handles UAC elevation prompts for installers/apps."),
            ["BrokerInfrastructure"] = Core("Background broker for UWP app lifecycle/notifications."),
            ["UserManager"] = Core("Manages user session state for the modern Windows shell."),
            ["StateRepository"] = Core("State Repository - the app metadata database backing Store/UWP apps."),
            ["AppXSvc"] = Core("AppX Deployment Service - installs/updates/removes UWP (Store) apps."),
            ["ClipSVC"] = Core("Client License Service - licensing for Store apps; some apps won't run without it."),
            ["Sppsvc"] = Core("Software Protection - Windows/Office activation and licensing."),
            ["SystemEventsBroker"] = Core("Brokers system event notifications for UWP apps."),
            ["TrustedInstaller"] = Core("Windows Modules Installer - installs/removes/updates Windows components and updates."),

            // ---- Security ------------------------------------------------------------------ //
            ["WinDefend"] = Security("Windows Defender Antivirus - real-time malware protection. Only disable if you run another AV."),
            ["MpsSvc"] = Security("Windows Defender Firewall - filters inbound/outbound network traffic."),
            ["BFE"] = Security("Base Filtering Engine - enforces firewall/IPsec policy; most network security depends on it."),
            ["wscsvc"] = Security("Security Center - reports AV/firewall status shown in Windows Security."),
            ["KeyIso"] = Security("CNG Key Isolation - isolates private keys for crypto operations."),
            ["VaultSvc"] = Security("Credential Manager - stores saved web/Windows credentials."),
            ["SCardSvr"] = Security("Smart Card - lets Windows read smart cards for logon/signing."),
            ["SCPolicySvc"] = Security("Smart Card Removal Policy - locks the session when a smart card is removed."),
            ["EFS"] = Security("Encrypting File System - transparent file/folder encryption support."),
            ["CertPropSvc"] = Security("Propagates certificates from inserted smart cards to the cert store."),
            ["Netlogon"] = Security("Domain authentication support (Active Directory environments)."),
            ["Kdc"] = Security("Kerberos Key Distribution Center - domain controllers only."),
            ["AppIDSvc"] = Security("Application Identity - evaluates AppLocker rules."),
            ["TokenBroker"] = Security("Manages sign-in tokens for connected (Microsoft/work/school) accounts."),
            ["WdNisSvc"] = Security("Windows Defender Network Inspection - inspects network traffic for exploits."),
            ["Sense"] = Security("Microsoft Defender for Endpoint sensor (enterprise environments)."),
            ["SgrmBroker"] = Security("System Guard Runtime Monitor - platform integrity checks."),
            ["WbioSrvc"] = Security("Windows Biometric Service - fingerprint/face sign-in (Windows Hello)."),
            ["EapHost"] = Security("Extensible Authentication Protocol - 802.1X/VPN authentication methods."),
            ["IKEEXT"] = Security("IKE and AuthIP IPsec Keying Modules - VPN/IPsec key exchange."),
            ["PolicyAgent"] = Security("IPsec Policy Agent - applies IPsec policies for authenticated/encrypted traffic."),
            ["WEPHOSTSVC"] = Security("Windows Encryption Provider Host - supports BitLocker/device encryption tooling."),

            // ---- Network -------------------------------------------------------------------- //
            ["Dhcp"] = Network("Requests/renews an IP address lease from a DHCP server."),
            ["Dnscache"] = Network("DNS Client - resolves and caches domain name lookups for the whole machine."),
            ["nsi"] = Network("Network Store Interface - notifies apps of network configuration changes."),
            ["NlaSvc"] = Network("Network Location Awareness - identifies network changes and connectivity type."),
            ["netprofm"] = Network("Network List Service - tracks known networks and their connectivity status."),
            ["LanmanServer"] = Network("Server - shares this machine's files/printers over the network (SMB server)."),
            ["LanmanWorkstation"] = Network("Workstation - connects to shared files/printers on other machines (SMB client)."),
            ["lmhosts"] = Network("TCP/IP NetBIOS Helper - legacy NetBIOS name resolution."),
            ["iphlpsvc"] = Network("IP Helper - IPv6 transition technologies (Teredo, 6to4, ISATAP)."),
            ["Netman"] = Network("Network Connections - manages objects in Network Connections folder."),
            ["NcbService"] = Network("Network Connection Broker - brokers network access for UWP apps."),
            ["RasMan"] = Network("Remote Access Connection Manager - manages dial-up/VPN connections."),
            ["RemoteAccess"] = Network("Routing and Remote Access - VPN/NAT routing (rarely needed on a client PC)."),
            ["SstpSvc"] = Network("Secure Socket Tunneling Protocol - SSTP VPN support."),
            ["WwanSvc"] = Network("WWAN AutoConfig - manages mobile broadband (cellular) connections."),
            ["WlanSvc"] = Network("WLAN AutoConfig - manages Wi-Fi adapters and connection profiles."),
            ["Dot3svc"] = Network("Wired AutoConfig - 802.1X authentication on wired Ethernet."),
            ["SharedAccess"] = Network("Internet Connection Sharing (ICS) - shares this PC's connection with others."),
            ["upnphost"] = Network("UPnP Device Host - allows UPnP devices to be hosted on this PC."),
            ["SSDPSRV"] = Network("SSDP Discovery - discovers UPnP devices on the local network."),
            ["FDResPub"] = Network("Function Discovery Resource Publication - publishes this PC for network discovery."),
            ["fdPHost"] = Network("Function Discovery Provider Host - discovers networked devices/services."),
            ["WebClient"] = Network("WebDAV Client - lets Explorer connect to WebDAV network shares."),
            ["WMPNetworkSvc"] = Network("Windows Media Player Network Sharing - shares the media library on the network."),
            ["wcncsvc"] = Network("Windows Connect Now - configures wireless settings (e.g. for WPS setup)."),
            ["QWAVE"] = Network("Quality Windows Audio Video Experience - prioritizes network A/V traffic."),
            ["workfolderssvc"] = Network("Work Folders - syncs a work folder to/from a company file server."),
            ["TermService"] = Network("Remote Desktop Services - allows incoming Remote Desktop connections."),
            ["SessionEnv"] = Network("Remote Desktop Configuration - session setup/config for Remote Desktop."),
            ["UmRdpService"] = Network("Remote Desktop UMRDP - user-mode RDP redirection support."),
            ["PNRPsvc"] = Network("Peer Name Resolution Protocol - peer-to-peer name resolution (rarely used)."),

            // ---- Storage --------------------------------------------------------------------- //
            ["VSS"] = Storage("Volume Shadow Copy - backs Windows Backup, System Restore, and 'Previous Versions'."),
            ["swprv"] = Storage("Microsoft Software Shadow Copy Provider - software-based shadow copy support."),
            ["vds"] = Storage("Virtual Disk - low-level disk/volume management used by Disk Management."),
            ["StorSvc"] = Storage("Storage Service - external storage (USB drives, SD cards) notifications and tiering."),
            ["TieringEngineService"] = Storage("Storage Tiers Management - moves data between fast/slow tiers on Storage Spaces."),
            ["defragsvc"] = Storage("Optimize Drives - scheduled disk defragmentation/TRIM."),
            ["smphost"] = Storage("Storage Spaces SMP - management for Storage Spaces pools."),
            ["SDRSVC"] = Storage("Windows Backup - runs File History / Windows Backup jobs."),
            ["CscService"] = Storage("Offline Files - caches network files for offline access."),

            // ---- Hardware -------------------------------------------------------------------- //
            ["Audiosrv"] = Hardware("Windows Audio - manages audio for apps. Disabling it mutes all system/app sound."),
            ["AudioEndpointBuilder"] = Hardware("Windows Audio Endpoint Builder - manages audio devices for Windows Audio."),
            ["Spooler"] = Hardware("Print Spooler - manages print jobs and installed printers."),
            ["PrintNotify"] = Hardware("Printer Extensions and Notifications - printer install/status notifications."),
            ["bthserv"] = Hardware("Bluetooth Support Service - discovery and pairing of Bluetooth devices."),
            ["BluetoothUserService"] = Hardware("Per-user Bluetooth features (audio, notifications)."),
            ["BthAvctpSvc"] = Hardware("AVCTP - Bluetooth audio/remote-control transport (headsets, etc.)."),
            ["stisvc"] = Hardware("Windows Image Acquisition - scanner/camera image transfer."),
            ["WiaRpc"] = Hardware("Still Image Acquisition RPC - per-session scanner/camera support."),
            ["hidserv"] = Hardware("Human Interface Device Service - special buttons/dials on keyboards/remotes."),
            ["ShellHWDetection"] = Hardware("Shell Hardware Detection - AutoPlay prompts for removable media."),
            ["WPDBusEnum"] = Hardware("Portable Device Enumerator - detects portable devices (phones, cameras) for sync."),
            ["TabletInputService"] = Hardware("Touch Keyboard and Handwriting Panel - on-screen keyboard/handwriting input."),
            ["Fax"] = Hardware("Fax - send/receive faxes (only relevant with fax hardware/software installed)."),
            ["TapiSrv"] = Hardware("Telephony API - legacy telephony/modem/VoIP dialing support."),
            ["SensrSvc"] = Hardware("Sensor Monitoring Service - collects data from ambient light/other sensors."),
            ["SensorService"] = Hardware("Sensor Service - broker for apps that use hardware sensors."),
            ["SensorDataService"] = Hardware("Sensor Data Service - delivers sensor readings to apps."),
            ["lfsvc"] = Hardware("Geolocation Service - supplies location data to apps that request it."),
            ["RmSvc"] = Hardware("Radio Management - handles the Airplane Mode / radio hardware switch."),
            ["SEMgrSvc"] = Hardware("Payments and NFC/SE Manager - NFC tap-to-pay hardware support."),

            // ---- UI / Shell ------------------------------------------------------------------- //
            ["Themes"] = UI("Manages visual themes (colors, sounds, icons). Cosmetic only."),
            ["UxSms"] = UI("Desktop Window Manager Session Manager - manages the DWM (visual effects) service."),
            ["SharedRealitySvc"] = UI("Spatial Data Service - supports Mixed Reality/spatial features."),

            // ---- Background / maintenance ---------------------------------------------------- //
            ["wuauserv"] = Background("Windows Update - detects, downloads, installs OS updates."),
            ["UsoSvc"] = Background("Update Orchestrator Service - schedules/coordinates Windows Update activity."),
            ["WaaSMedicSvc"] = Background("Windows Update Medic Service - repairs Windows Update components if corrupted."),
            ["BITS"] = Background("Background Intelligent Transfer Service - throttled background downloads (used by Update, others)."),
            ["WerSvc"] = Background("Windows Error Reporting - collects and reports application/OS crash information."),
            ["PcaSvc"] = Background("Program Compatibility Assistant - flags/fixes compatibility issues in older apps."),
            ["DPS"] = Background("Diagnostic Policy Service - detects/diagnoses/resolves problems with Windows components."),
            ["WdiServiceHost"] = Background("Diagnostic Service Host - runs diagnostic detection/troubleshooting modules."),
            ["WdiSystemHost"] = Background("Diagnostic System Host - system-level diagnostics/troubleshooting."),
            ["Wecsvc"] = Background("Windows Event Collector - collects events from remote machines (usually inactive)."),
            ["MSDTC"] = Background("Distributed Transaction Coordinator - coordinates transactions across databases/resources (dev/DB scenarios)."),
            ["InstallService"] = Background("Microsoft Store Install Service - installs/updates/removes Store apps in the background."),
            ["LicenseManager"] = Background("Manages licenses for Store apps/Windows features."),
            ["gupdate"] = Background("Google Update - keeps Google software (e.g. Chrome) up to date."),
            ["gupdatem"] = Background("Google Update (on-demand trigger) - companion to gupdate."),
            ["edgeupdate"] = Background("Microsoft Edge Update - keeps Edge up to date."),
            ["edgeupdatem"] = Background("Microsoft Edge Update (on-demand trigger) - companion to edgeupdate."),
            ["wisvc"] = Background("Windows Insider Service - manages Insider Preview builds/feedback."),

            // ---- Auxiliary: doesn't affect core OS function ----------------------------------- //
            ["DiagTrack"] = Auxiliary("Connected User Experiences and Telemetry - collects usage/diagnostic data sent to Microsoft. " +
                                       "Commonly disabled for privacy/performance with no functional loss."),
            ["dmwappushservice"] = Auxiliary("Device Management Wireless Application Protocol push message routing - part of the telemetry/MDM pipeline."),
            ["SysMain"] = Auxiliary("Superfetch/SysMain - preloads frequently used apps into memory to speed launches. " +
                                     "Often disabled on SSDs, where the caching benefit is smaller."),
            ["WSearch"] = Auxiliary("Windows Search - indexes files/mail for fast Start-menu/Explorer search. " +
                                     "Disabling stops indexing; search still works but scans on demand (slower)."),
            ["LanguageComponentsInstaller"] = Auxiliary("Installs additional language/keyboard packs on demand - not needed unless adding input languages."),
            ["MapsBroker"] = Auxiliary("Downloaded Maps Manager - manages offline maps downloaded for the Maps app."),
            ["RetailDemo"] = Auxiliary("Retail Demo Service - runs the in-store demo experience (irrelevant on a personal PC)."),
            ["CDPSvc"] = Auxiliary("Connected Devices Platform - lets this PC discover/interact with your other Microsoft-account devices."),
            ["CDPUserSvc"] = Auxiliary("Connected Devices Platform User Service - per-user companion to CDPSvc."),
            ["WpnService"] = Auxiliary("Windows Push Notifications System - delivers toast/tile notifications."),
            ["WpnUserService"] = Auxiliary("Windows Push Notifications User Service - per-user companion to WpnService."),
            ["PimIndexMaintenanceSvc"] = Auxiliary("Contact Data - indexes contacts for search/People features."),
            ["UnistoreSvc"] = Auxiliary("User Data Storage - backs structured user data (notifications, etc.) for apps."),
            ["UserDataSvc"] = Auxiliary("User Data Access - lets apps access unified contacts/calendar/messaging data."),
            ["MessagingService"] = Auxiliary("Messaging Service - SMS/messaging sync for the Phone Link / Messaging experience."),
            ["OneSyncSvc"] = Auxiliary("Sync Host - syncs mail/contacts/calendar for connected accounts in the background."),
            ["PhoneSvc"] = Auxiliary("Phone Service - telephony/Phone Link state for the modern phone experience."),
            ["TrkWks"] = Auxiliary("Distributed Link Tracking Client - keeps shortcuts working when files move (NTFS)."),
            ["XblAuthManager"] = Auxiliary("Xbox Live Auth Manager - sign-in for Xbox/gaming features."),
            ["XblGameSave"] = Auxiliary("Xbox Live Game Save - syncs game saves to the cloud."),
            ["XboxNetApiSvc"] = Auxiliary("Xbox Live Networking Service - multiplayer/network features for Xbox app games."),
            ["XboxGipSvc"] = Auxiliary("Xbox Accessory Management Service - support for Xbox controllers/accessories."),
            ["WalletService"] = Auxiliary("Wallet Service - backs the (largely retired) Wallet app."),
            ["shpamsvc"] = Auxiliary("Shared PC Account Manager - account cleanup on shared/kiosk PCs."),
            ["UevAgentService"] = Auxiliary("User Experience Virtualization - enterprise settings roaming (inactive on most PCs)."),
            ["wercplsupport"] = Auxiliary("Problem Reports Control Panel Support - backs the Problem Reports UI."),
        };
    }
}
