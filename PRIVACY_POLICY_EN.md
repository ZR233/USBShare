# USBShare Privacy Policy

Last updated: March 16, 2026

This Privacy Policy applies to USBShare ("the App"). USBShare is a Windows desktop application that allows you to share local USB devices with a Linux host you specify through an SSH tunnel and perform USB over IP attach operations.

This Privacy Policy explains:

- what information the App may process
- how that information is used, stored, and protected
- when information may be disclosed
- how you can manage and delete related data

## 1. Information We Process

Depending on how you use the App, USBShare may process the following information:

### 1.1 Information you provide

- Remote connection settings, such as remote name, host address, port, username, authentication method, private key path, and tunnel port
- SSH passwords, private key passphrases, and sudo passwords that you choose to save
- USB device or USB hub sharing rules that you enable in the App

### 1.2 Device and system information read during operation

- Local USB devices, USB hubs, device instance IDs, bus IDs, device descriptions, and sharing status
- `usbipd`-related status information
- Local application settings required for operation, such as polling interval, selected remote target, and automatic start preferences

### 1.3 Information sent to your chosen remote server

When you enable sharing, the App sends information necessary to provide that function to the Linux host that you configure, including:

- Authentication information required to establish the SSH connection
- Commands and parameters needed to perform `usbip attach`, `usbip detach`, `sudo`, port forwarding, and environment checks
- USB sharing identifiers such as bus IDs

## 2. How We Use Information

The App processes the information above only to provide and maintain the functionality you request, including:

- storing and managing remote connection settings
- establishing SSH connections and reverse port forwarding
- running local `usbipd bind/unbind` operations
- running remote `usbip attach/detach` operations on your Linux host
- identifying and displaying local USB topology and shareable device status
- saving your app preferences and restoring sharing sessions when applicable
- diagnosing functional issues such as connection failures, permission problems, or attach failures

The current version of the App does not process your personal information for in-app advertising, user profiling, behavioral analytics, or developer-operated telemetry reporting.

## 3. How We Store and Protect Information

### 3.1 Local storage

The App primarily stores data locally on your Windows device. Based on the current implementation:

- General configuration is stored by default at `%LOCALAPPDATA%\\USBShare\\config.json`
- Saved sensitive credentials are stored by default under `%LOCALAPPDATA%\\USBShare\\secrets\\`

### 3.2 Security measures

- Saved SSH passwords, private key passphrases, and sudo passwords are encrypted using Windows Data Protection API (DPAPI) for the current Windows user
- The App communicates with your chosen remote server over SSH
- The App does not, by default, upload your configuration or credentials to cloud services operated by the developer

Please note that no software or network transmission can be guaranteed to be absolutely secure. If your remote host, SSH environment, private key files, operating system account, or network environment is insecure, the related risks may be outside the App's control.

## 4. When We Share or Disclose Information

Except in the situations below, the App does not sell or share your personal information with the developer, advertisers, or unrelated third parties:

- transmitting information necessary to provide USB sharing to the remote Linux host that you explicitly configure
- interacting with Windows, `usbipd`, SSH components, and related system capabilities to perform the functions you request
- complying with applicable laws, regulations, court orders, or lawful governmental requests
- protecting the legal rights, safety, and security of the App, users, the developer, or the public when reasonably necessary

Third-party software, services, or system components, such as Windows, SSH, `usbipd-win`, Linux, `usbip`, `sudo`, and any remote server you build, host, or administer, may process data under their own privacy and security terms. This Privacy Policy does not cover the independent practices of those third parties.

## 5. Data Retention

How long data is retained generally depends on how you use and manage the App:

- Remote settings and app preferences remain stored locally until you modify or delete them
- Saved credentials remain stored locally until you delete the corresponding remote configuration in the App or manually remove the local data files
- When you delete a remote configuration in the App, saved SSH and sudo credentials associated with that remote are also deleted

If you want to remove all locally stored data, you can delete relevant settings in the App or manually remove the files under `%LOCALAPPDATA%\\USBShare\\`.

## 6. Your Choices and Controls

You can control your information in the following ways:

- decide whether to save SSH passwords, private key passphrases, or sudo passwords in the App
- edit or delete remote connection settings
- modify or disable automatic start sharing, polling, and other local preferences
- stop sharing and disconnect related sessions
- delete local configuration files and locally encrypted credential files
- remove related logs, authorization records, or USB attach records on your own remote Linux host

Because the App primarily processes data locally on your device and on remote hosts under your control, many access, correction, deletion, and restriction actions can typically be completed directly by you on your own systems.

## 7. Children's Privacy

The App is not designed for children and is not specifically directed to children under the age of 13. The developer does not knowingly collect personal information from children through the App.

## 8. Changes to This Privacy Policy

This Privacy Policy may be updated if the App's features, data processing practices, legal requirements, or store submission requirements change. When updated, the "Last updated" date at the top of this page will also be revised.

If a change is material, the developer will make reasonable efforts to provide notice through the project repository, release notes, app listing, or another appropriate channel.

## 9. Contact

If you have questions about this Privacy Policy or how the App handles data, you can contact the project maintainer through:

- Project home: <https://github.com/ZR233/USBShare>
- Issue tracker: <https://github.com/ZR233/USBShare/issues>

## 10. Applicability

This Privacy Policy is intended to help users understand how the current version of USBShare handles data and is drafted to align, as far as practical, with common Microsoft Store transparency expectations for privacy disclosures. It is not legal advice and does not replace any legal obligations that may apply to you in a specific jurisdiction.

If you plan to distribute the App in a specific country or region, or if the publisher is a company, studio, or organization, you should review and adapt this Privacy Policy based on your actual business operations and applicable laws.
