# Project Nexus — Feature ideas and UX backlog

Feature and interaction ideas for consideration. The suggested milestones below are historical. Use the [current plan](project-nexus-plan-v0.4.md) and [decision log](docs/decision-log.md) for implementation scope.

## 0. Product influences

| Reference | Ideas to adapt | Limitations identified in the original review |
|---|---|---|
| Zalo | Invitation-based connections, community owners and deputies, ownership transfer, quick image/PDF sharing and familiar interactions for Vietnamese staff. | Personal and professional conversations mix. Files can expire and ownership can remain with departing employees. |
| Slack | Side-panel threads, hover actions, quick reactions, pins, saved items, status, mute, Ctrl+K navigation and Ctrl+V image paste. | The review cited a 90-day free history limit and difficulty mapping a flat channel list to organizational units. |
| Microsoft Teams | The early concept used Workspace → Community, acknowledged announcements, left navigation, conversation files, separate administrative and content access, member removal and emergency admin transfers. | Heavy menus, perceived performance and free-service lifecycle concerns. |

With one developer, choose one primary interaction for each task. Keep replies and navigation shallow. The accepted model now uses Community → Channel.

## 1. Chat interactions

| ID | Feature and purpose | Effort for one C# developer | Original milestone | Prototype status |
|---|---|---|---|---|
| UX-01 | Hover actions and quick reactions. Use 👀 for reviewing and ✅ for done to reduce acknowledgment messages. | Low | M02 | In end-user prototype |
| UX-02 | Open a thread on the right to keep the main discussion readable in groups of 20–50. | Medium | M02 | In end-user prototype |
| UX-03 | Pin project links, meeting times and rules at the top of the chat. | Low | M02 | In end-user prototype |
| UX-04 | Save messages for follow-up without sending them to yourself. | Low | M02 | In end-user prototype |
| UX-05 | Correct or recall your own messages. Record `edited_at` / `deleted_at`. | Low | M02 | In end-user prototype |
| UX-06 | Show custom status such as In a meeting, On site or On leave next to the name. | Low | M02 | In end-user prototype |
| UX-07 | Mute a busy conversation or use Do Not Disturb while retaining membership. | Low | M02 | In end-user prototype |
| UX-08 | Paste screenshots with Ctrl+V and drag files into chat. Support safe server-side image re-encoding. | Medium | M02 | In end-user prototype |
| UX-09 | Use a New messages divider and manual mark-as-unread to resume where you left off. | Low | M02 | Idea |
| UX-10 | Switch DMs or groups with Ctrl+K, arrow keys and Enter. | Low | M02 | Idea |
| UX-11 | Remind me in 30 minutes or at 9 tomorrow. Requires a background job. | Medium | M03+ | Idea |
| UX-12 | Compose after hours and schedule delivery for the next morning. | Medium | M03+ | Idea |

## 2. Community features

| ID | Feature | Original milestone |
|---|---|---|
| CM-01 | Polls in community posts and group chats for meeting times, activities and internal surveys. | M03 |
| CM-02 | Optionally map department changes to community membership. | M03+ |
| CM-03 | Export acknowledgment rates and outstanding recipients to CSV/Excel for HR. | M03+ |
| CM-04 | Pin important policies and guides to the top of the feed. | M03 |

## 3. Longer-term ideas

- Events and RSVPs for organizations or branches.
- A searchable, versioned HR/IT policy knowledge base.
- A .NET MAUI or Capacitor mobile wrapper if iOS PWA push delivery falls short in the pilot.
- HRM/CRM integrations and AI-assisted policy lookup when a real use case and suitable source data exist, as described in proposal §14.
