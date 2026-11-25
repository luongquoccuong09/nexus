---
id: M6-D01
title: "Calendar components and self-hosted media qualification"
summary: "Use FullCalendar Standard, iCal.NET, and single-node LiveKit with private provider signaling and Nexus-enforced grant revocation."
date: 2026-11-25
decided-by: team
status: accepted
---

# Calendar components and self-hosted media qualification

## Calendar selection

Use MIT-licensed FullCalendar Standard React `7.1.5` for week, month, and agenda views, with `temporal-polyfill` `1.0.7`. Keep the calendar and its CSS behind `React.lazy` and a dynamic import. Use a keyboard-accessible Nexus view picker at all widths because the built-in toolbar hides the view choices at narrow widths. Enable keyboard activation of event entries and use Nexus surface and text tokens. The isolated production build emitted a 307.57 kB calendar JavaScript chunk (85.22 kB gzip) and 20.67 kB CSS (4.54 kB gzip). Both loaded when the calendar opened. [FullCalendar React](https://fullcalendar.io/docs/react) · [License](https://fullcalendar.io/license)

Use MIT-licensed iCal.NET `5.2.4` for recurrence evaluation and `.ics` serialization. Keep the event's time zone alongside UTC instants and apply recurrence in that zone. The prototype kept 09:00 local through both daylight-saving transitions in New York. An export and reload preserved `VTIMEZONE`, `RECURRENCE-ID`, `EXDATE`, and the series UID. A bounded query excluded the occurrence at the exclusive endpoint. [iCal.NET release](https://github.com/ical-org/ical.net/releases/tag/v5.2.4) · [License](https://github.com/ical-org/ical.net/blob/v5.2.4/license.md)

## Media comparison

| Requirement | LiveKit | Jitsi Meet |
|---|---|---|
| Version and license | Apache-2.0 server `1.13.11`, browser SDK `2.25.0`, and server SDK `2.20.1` in the qualification harness. Single-node routing ran with no Redis. | Apache-2.0 Docker bundle `stable-11509-1`. Its four core services are Web, Prosody, Jicofo, and Jitsi Videobridge. |
| Joining and direct-call answer | In five Edge runs, click-to-bidirectional audio/video took 718–899 ms including the dynamically loaded production SDK chunk. | Click-to-bidirectional audio/video took 4,592–4,706 ms with P2P and 3,703–9,602 ms in five working SFU samples. The timed path includes loading the IFrame API. |
| Preview and compact view | Same-origin JavaScript SDK can preview before joining. A compact view retained the room, track identities, and incoming media in WebView2. | IFrame commands expose device selection and screen share. Same-frame resize retained active media in Edge. The WebView2 candidate joined and retained the iframe when reduced, and its share action fired the native capture event for the cross-origin frame. |
| Participant removal and previously issued credentials | Upstream documents that self-hosted removal and permission changes leave issued credentials valid. The isolated signaling proxy denied the revoked grant while the provider removed the active participant. | The room-bound JWT check denied guest and unrelated-room admission. The moderator removed an active participant, and the default deployment allowed that participant to rejoin with their previously issued, unexpired credential. |
| CPU and memory | Docker samples were 0.03% CPU / 15.46 MiB at idle and 10.63–11.00% / 58.17–59.82 MiB with two publishing video clients. | The four-container snapshot totaled 0.28% CPU / 485.07 MiB at idle. Media samples are in the handoff. |
| Operating model | Pin the image digest and deploy one server with TLS signaling, ICE over UDP/TCP, and TURN/TLS for restrictive networks. Upgrade the server and SDK pins through the media test matrix. | Pin the bundle and all matching images. Update them together after reviewing the release configuration. Restrictive networks need an appropriate TURN/TCP or TURN/TLS deployment. |

[LiveKit release](https://github.com/livekit/livekit/releases/tag/v1.13.11) · [LiveKit configuration and license](https://github.com/livekit/livekit/blob/v1.13.11/config-sample.yaml) · [LiveKit credential lifecycle](https://docs.livekit.io/frontends/reference/tokens-grants/#self-hosted-deployments) · [LiveKit ports](https://docs.livekit.io/transport/self-hosting/ports-firewall/) · [Jitsi release](https://github.com/jitsi/docker-jitsi-meet/releases/tag/stable-11509-1) · [Jitsi license](https://github.com/jitsi/docker-jitsi-meet/blob/stable-11509-1/LICENSE) · [Jitsi IFrame commands](https://jitsi.github.io/handbook/docs/dev-guide/dev-guide-iframe-commands/) · [Jitsi Docker and TURN configuration](https://jitsi.github.io/handbook/docs/devops-guide/devops-guide-docker/)

At an assumed 1.5 Mb/s video and 0.05 Mb/s audio per participant, two SFU participants receive 3.10 Mb/s in total. That is 1.395 GB of outbound media per call-hour before protocol overhead. At an illustrative rate of $0.05–$0.09 per GB, outbound media costs about $0.07–$0.13 per call-hour before compute or relay charges. Direct peer-to-peer media uses provider bandwidth for signaling. Relaying through TURN adds media egress, and separate SFU and TURN placements can add an inter-host leg. Group calls, adaptive layers, screen sharing, and actual relay rates require measurements for the selected deployment.

Use single-node LiveKit for scheduled meetings, Meet now, and direct calls. The measured two-account browser call met the one-second target with the lazy-loaded SDK. WebView2 exchanged camera, microphone, and screen-share media. A dedicated test with blocked UDP sustained media over ICE/TCP. Keep TURN/TLS and capacity verification in the release gate for the target deployment. [M6 handoff](../implementation-plan/m6-calendar-meetings/HANDOFF-m6-calendar-meetings.md)

Nexus owns room membership, grants, and revocation. Keep provider signaling and administrative APIs private. Recheck the active account, current resource access, and session-bound grant before every signaling connection and reconnect, including when the SDK presents a refreshed provider credential. Revoke the server-side grant and remove the active provider participant on access changes. Bind grants to the current publishing policy so an older grant cannot restore screen-share permission. Keep credentials out of application, proxy, and provider logs. Load the media UI and SDK dynamically, and keep one mounted room session across compact and expanded views.
