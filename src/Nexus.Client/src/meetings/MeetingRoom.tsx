import { useEffect, useRef, useState } from 'react';
import { ConnectionState, createLocalAudioTrack, createLocalVideoTrack, DisconnectReason, type LocalTrack,
  LogLevel, type Participant, Room, RoomEvent, setLogLevel, Track } from 'livekit-client';
import { Mic, MicOff, MonitorUp, PhoneOff, Video, VideoOff } from 'lucide-react';
import { Avatar, Button, Modal } from '../ui';
import { endDirectCall } from '../calls/callApi';
import { endMeeting, joinMeeting, leaveMeeting, type MeetingIntent, MeetingRequestError, type MeetingSession, removeMeetingParticipant } from './meetingApi';
import { type MeetingDevices, selectedCapture } from './meetingDevices';
import { mediaSignalUrl } from './mediaSignal';
import './meetings.css';

setLogLevel(LogLevel.silent);

type MeetingDevice = 'microphone' | 'camera' | 'screen';
type MeetingInput = Exclude<MeetingDevice, 'screen'>;
type MeetingStatus = 'connecting' | 'connected' | 'reconnecting' | 'closed' | 'failed';

interface TrackView {
  id: string;
  track: Track;
}

interface ParticipantView {
  identity: string;
  name: string;
  own: boolean;
  tracks: TrackView[];
}

const meetingStatus: Record<MeetingStatus, string> = {
  connecting: 'Connecting to meeting…',
  connected: 'Connected',
  reconnecting: 'Reconnecting…',
  closed: 'Meeting disconnected.',
  failed: 'Unable to connect to the meeting.'
};

function participantView(participant: Participant, own: boolean): ParticipantView {
  const tracks: TrackView[] = [];
  for (const publication of participant.trackPublications.values()) {
    const track = publication.track;
    if (track && !publication.isMuted && (!own || track.kind === Track.Kind.Video)) {
      tracks.push({ id: publication.trackSid, track });
    }
  }
  return { identity: participant.identity, name: own ? 'You' : participant.name || 'Participant', own, tracks };
}

function MediaTrack({ track, name }: { track: Track; name: string }) {
  const element = useRef<HTMLMediaElement | null>(null);
  useEffect(() => {
    const target = element.current;
    if (!target) return;
    track.attach(target);
    return () => { track.detach(target); };
  }, [track]);
  return track.kind === Track.Kind.Audio
    ? <audio ref={audio => { element.current = audio; }} autoPlay />
    : <video ref={video => { element.current = video; }} autoPlay playsInline
        muted={track.isLocal} aria-label={`${track.source === Track.Source.ScreenShare ? 'Screen share' : 'Camera'} · ${name}`} />;
}

function mediaError(device: MeetingDevice, cause: unknown) {
  const type = cause instanceof Error ? cause.name : '';
  const label = device === 'screen' ? 'Screen sharing' : device === 'microphone' ? 'Microphone' : 'Camera';
  if (type === 'NotAllowedError' || type === 'PermissionDeniedError') {
    return device === 'screen'
      ? 'Screen sharing permission was denied or canceled. Select Share screen to try again.'
      : `${label} permission was denied. Update the permissions for Nexus and try again.`;
  }
  if (type === 'NotFoundError' || type === 'DevicesNotFoundError' || type === 'OverconstrainedError') {
    return `${label} is unavailable. Connect a device and try again.`;
  }
  if (type === 'NotReadableError' || type === 'TrackStartError') return `${label} is unavailable. Close any app using it and try again.`;
  return `Unable to change ${label.toLowerCase()}. Try again.`;
}

export default function MeetingRoom({ intent, selectedDevices, onClose }: {
  intent: MeetingIntent;
  selectedDevices?: MeetingDevices;
  onClose: () => void;
}) {
  const roomRef = useRef<Room | null>(null);
  const sessionRef = useRef<MeetingSession | null>(null);
  const actionRef = useRef<AbortController | null>(null);
  const deviceRequests = useRef(new Set<MeetingDevice>());
  const [session, setSession] = useState<MeetingSession | null>(null);
  const [status, setStatus] = useState<MeetingStatus>('connecting');
  const [participants, setParticipants] = useState<ParticipantView[]>([]);
  const [participantPage, setParticipantPage] = useState(0);
  const [devices, setDevices] = useState<Record<MeetingDevice, boolean>>({ microphone: false, camera: false, screen: false });
  const [pendingDevices, setPendingDevices] = useState<MeetingDevice[]>([]);
  const [needsAudio, setNeedsAudio] = useState(false);
  const [confirmEnd, setConfirmEnd] = useState(false);
  const [removeTarget, setRemoveTarget] = useState<ParticipantView | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  const isDirect = intent.kind === 'directCall';

  useEffect(() => {
    const controller = new AbortController();
    const microphoneSelection = selectedCapture(selectedDevices?.microphoneId);
    const cameraSelection = selectedCapture(selectedDevices?.cameraId);
    const room = new Room({
      adaptiveStream: true, dynacast: true, publishDefaults: { stopMicTrackOnMute: true },
      audioCaptureDefaults: microphoneSelection, videoCaptureDefaults: cameraSelection,
      audioOutput: selectedDevices?.speakerId ? { deviceId: selectedDevices.speakerId } : undefined
    });
    roomRef.current = room;
    setStatus('connecting');
    setSession(null);
    setParticipants([]);
    setParticipantPage(0);
    setError('');
    setNeedsAudio(false);
    const defaults: MeetingInput[] = [];
    if (intent.kind === 'directCall' || selectedDevices?.microphone) defaults.push('microphone');
    if (intent.kind === 'directCall' ? intent.callKind === 'Video' : selectedDevices?.camera) defaults.push('camera');
    setPendingDevices(defaults);
    const pending = deviceRequests.current;
    pending.clear();
    for (const device of defaults) pending.add(device);
    const unpublishedTracks = new Set<LocalTrack>();
    let abandoned = false;

    function releaseUnpublished() {
      abandoned = true;
      for (const track of unpublishedTracks) track.stop();
      unpublishedTracks.clear();
    }

    // This effect starts after a direct answer or an explicit join from the device check.
    // Prepare selected inputs alongside signaling, then publish after server admission.
    const deviceSetup = defaults.map(async device => {
      try {
        const track = await (device === 'microphone'
          ? createLocalAudioTrack(microphoneSelection)
          : createLocalVideoTrack(cameraSelection));
        if (abandoned || controller.signal.aborted) {
          track.stop();
          return { device };
        }
        unpublishedTracks.add(track);
        return { device, track };
      } catch (cause) {
        return { device, error: mediaError(device, cause) };
      }
    });

    function refresh() {
      if (controller.signal.aborted) return;
      setParticipants([participantView(room.localParticipant, true), ...Array.from(room.remoteParticipants.values(), p => participantView(p, false))]);
      setDevices({
        microphone: room.localParticipant.isMicrophoneEnabled,
        camera: room.localParticipant.isCameraEnabled,
        screen: room.localParticipant.isScreenShareEnabled
      });
      setNeedsAudio(!room.canPlaybackAudio);
    }
    function connectionChanged(connection: ConnectionState) {
      if (controller.signal.aborted) return;
      if (connection === ConnectionState.Connected) {
        setStatus('connected');
        refresh();
      } else if (connection === ConnectionState.Reconnecting || connection === ConnectionState.SignalReconnecting) {
        setStatus('reconnecting');
      }
    }
    function disconnected(reason?: DisconnectReason) {
      if (controller.signal.aborted) return;
      setParticipants([]);
      setNeedsAudio(false);
      setStatus('closed');
      if (reason === DisconnectReason.PARTICIPANT_REMOVED) setError('You were removed from this meeting.');
      if (reason === DisconnectReason.ROOM_DELETED) setError('The meeting has ended.');
    }
    async function publishSelectedInputs() {
      const failures: string[] = [];
      // The microphone is first. A slower camera cannot delay voice media.
      for (const preparation of deviceSetup) {
        const { device, error, track } = await preparation;
        if (abandoned || controller.signal.aborted) return;
        if (error) failures.push(error);
        if (track) {
          try {
            await room.localParticipant.publishTrack(track);
          } catch (cause) {
            track.stop();
            failures.push(mediaError(device, cause));
          } finally {
            unpublishedTracks.delete(track);
          }
        }
        if (abandoned || controller.signal.aborted) return;
        pending.delete(device);
        setPendingDevices([...pending]);
        setError(failures.join(' '));
        refresh();
      }
    }
    room.on(RoomEvent.ConnectionStateChanged, connectionChanged)
      .on(RoomEvent.Disconnected, disconnected)
      .on(RoomEvent.ParticipantConnected, refresh)
      .on(RoomEvent.ParticipantDisconnected, refresh)
      .on(RoomEvent.ParticipantNameChanged, refresh)
      .on(RoomEvent.TrackSubscribed, refresh)
      .on(RoomEvent.TrackUnsubscribed, refresh)
      .on(RoomEvent.TrackMuted, refresh)
      .on(RoomEvent.TrackUnmuted, refresh)
      .on(RoomEvent.LocalTrackPublished, refresh)
      .on(RoomEvent.LocalTrackUnpublished, refresh)
      .on(RoomEvent.AudioPlaybackStatusChanged, refresh);
    if (deviceSetup.length) room.once(RoomEvent.SignalConnected, () => void publishSelectedInputs());

    void (async () => {
      try {
        const { token, signalPath, ...admission } = await joinMeeting(intent, controller.signal);
        if (controller.signal.aborted) {
          void leaveMeeting(admission).catch(() => {});
          return;
        }
        sessionRef.current = admission;
        setSession(admission);
        await room.connect(mediaSignalUrl(window.location.origin, signalPath), token);
        refresh();
      } catch (cause) {
        if (controller.signal.aborted) return;
        releaseUnpublished();
        const admission = sessionRef.current;
        sessionRef.current = null;
        void room.dispose().catch(() => {});
        if (admission) void leaveMeeting(admission).catch(() => {});
        setStatus('failed');
        setError(cause instanceof MeetingRequestError ? cause.message : 'Check your connection and meeting access, then try again.');
      }
    })();

    return () => {
      controller.abort();
      actionRef.current?.abort();
      roomRef.current = null;
      releaseUnpublished();
      const admission = sessionRef.current;
      sessionRef.current = null;
      void room.dispose().catch(() => {});
      if (admission) void leaveMeeting(admission).catch(() => {});
    };
  }, [attempt, intent, selectedDevices]);

  async function toggle(device: MeetingDevice) {
    const room = roomRef.current;
    if (!room || room.state !== ConnectionState.Connected || deviceRequests.current.has(device)) return;
    deviceRequests.current.add(device);
    setPendingDevices([...deviceRequests.current]);
    setError('');
    try {
      const local = room.localParticipant;
      if (device === 'microphone') await local.setMicrophoneEnabled(!local.isMicrophoneEnabled);
      if (device === 'camera') await local.setCameraEnabled(!local.isCameraEnabled);
      if (device === 'screen') await local.setScreenShareEnabled(!local.isScreenShareEnabled, { audio: false });
      if (roomRef.current === room) {
        setDevices({ microphone: local.isMicrophoneEnabled, camera: local.isCameraEnabled, screen: local.isScreenShareEnabled });
      }
    } catch (cause) {
      if (roomRef.current === room) setError(mediaError(device, cause));
    } finally {
      if (roomRef.current === room) {
        deviceRequests.current.delete(device);
        setPendingDevices([...deviceRequests.current]);
      }
    }
  }

  async function enableAudio() {
    const room = roomRef.current;
    if (!room) return;
    setError('');
    try {
      await room.startAudio();
    } catch {
      if (roomRef.current === room) setError('Unable to play meeting audio. Check your speaker settings and try again.');
    }
  }

  async function runHostAction(target?: ParticipantView) {
    if (!session || actionRef.current) return;
    const controller = new AbortController();
    actionRef.current = controller;
    setBusy(true);
    setError('');
    try {
      if (target) {
        await removeMeetingParticipant(session, target.identity, controller.signal);
        if (!controller.signal.aborted) setRemoveTarget(null);
      } else {
        // The host has confirmed the end. Stop this client before revoking its credential.
        await roomRef.current?.disconnect();
        if (controller.signal.aborted) return;
        await endMeeting(session, controller.signal);
        if (controller.signal.aborted) return;
        sessionRef.current = null;
        onClose();
      }
    } catch (cause) {
      if (controller.signal.aborted) return;
      if (!target && roomRef.current?.state === ConnectionState.Disconnected) {
        sessionRef.current = null;
        await leaveMeeting(session).catch(() => {});
        if (controller.signal.aborted) return;
        setConfirmEnd(false);
        setStatus('failed');
      }
      setError(cause instanceof Error ? cause.message : 'Unable to update the meeting.');
    } finally {
      if (actionRef.current === controller) actionRef.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  async function closeRoom() {
    if (actionRef.current) return;
    if (intent.kind !== 'directCall') {
      onClose();
      return;
    }
    const controller = new AbortController();
    actionRef.current = controller;
    setBusy(true);
    setError('');
    try {
      const ended = await endDirectCall({ id: intent.callId, organizationId: intent.organizationId }, controller.signal);
      if (!controller.signal.aborted) {
        sessionRef.current = null;
        if (ended) intent.onEnded(ended);
        onClose();
      }
    } catch (cause) {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Unable to end the call. Try again.');
      }
    } finally {
      if (actionRef.current === controller) actionRef.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  const connected = status === 'connected';
  const sharingAvailable = Boolean(navigator.mediaDevices?.getDisplayMedia);
  const canManage = !isDirect && connected && Boolean(session?.canManage);
  const title = session?.title || intent.title;
  const canRetry = status === 'failed';
  const pageSize = 50;
  const lastPage = Math.max(0, Math.ceil(participants.length / pageSize) - 1);
  const page = Math.min(participantPage, lastPage);
  const visibleParticipants = participants.slice(page * pageSize, (page + 1) * pageSize);
  return (
    <Modal open size="xl" title={title} description={isDirect ? 'Direct call' : 'Meeting'}
      onOpenChange={open => { if (!open) void closeRoom(); }}>
      <div className="nx-meeting">
        <p className="nx-muted" role="status">{isDirect && status === 'connecting' ? 'Connecting call…' : meetingStatus[status]}</p>
        {error && <p className="nx-error" role="alert">{error}</p>}
        {participants.flatMap(participant => participant.tracks
          .filter(({ track }) => track.kind === Track.Kind.Audio)
          .map(({ id, track }) => <MediaTrack key={id} track={track} name={participant.name} />))}
        {visibleParticipants.length > 0 && (
          <ul className="nx-meeting-participants" aria-label="Meeting participants">
            {visibleParticipants.map(participant => (
              <li key={participant.identity} className="nx-meeting-participant">
                <div className="nx-meeting-media">
                  {!participant.tracks.some(({ track }) => track.kind === Track.Kind.Video) &&
                    <Avatar displayName={participant.name} size="lg" />}
                  {participant.tracks.filter(({ track }) => track.kind === Track.Kind.Video)
                    .map(({ id, track }) => <MediaTrack key={id} track={track} name={participant.name} />)}
                </div>
                <div className="nx-meeting-participant-label">
                  <strong>{participant.name}</strong>
                  {canManage && !participant.own && <Button size="sm" aria-label={`Remove ${participant.name}`}
                    onClick={() => { setRemoveTarget(participant); setConfirmEnd(false); }} disabled={busy}>Remove</Button>}
                </div>
              </li>
            ))}
          </ul>
        )}
        {lastPage > 0 && (
          <div className="nx-meeting-controls" role="group" aria-label="Participant pages">
            <Button size="sm" disabled={page === 0} onClick={() => setParticipantPage(page - 1)}>Previous participants</Button>
            <p role="status">Page {page + 1} of {lastPage + 1}</p>
            <Button size="sm" disabled={page === lastPage} onClick={() => setParticipantPage(page + 1)}>Next participants</Button>
          </div>
        )}
        {needsAudio && <Button size="sm" onClick={() => void enableAudio()}>Play meeting audio</Button>}
        {canManage && (confirmEnd || removeTarget) && (
          <div className="nx-meeting-confirmation" role="group" aria-label={removeTarget ? 'Confirm participant removal' : 'Confirm meeting end'}>
            <p>{removeTarget ? `Remove ${removeTarget.name} from this meeting?` : 'End the meeting for everyone?'}</p>
            <Button onClick={() => { setRemoveTarget(null); setConfirmEnd(false); }} disabled={busy}>Back</Button>
            <Button variant="danger" disabled={busy} onClick={() => void runHostAction(removeTarget ?? undefined)}>
              {removeTarget ? 'Confirm removal' : 'End for everyone'}
            </Button>
          </div>
        )}
        <div className="nx-meeting-controls" role="group" aria-label="Meeting controls">
          {(connected || status === 'reconnecting') && (
            <>
              <Button aria-pressed={devices.microphone} disabled={!connected || pendingDevices.includes('microphone')}
                onClick={() => void toggle('microphone')}>
                {devices.microphone ? <Mic aria-hidden="true" size={16} /> : <MicOff aria-hidden="true" size={16} />}
                {devices.microphone ? 'Turn off microphone' : 'Turn on microphone'}
              </Button>
              <Button aria-pressed={devices.camera} disabled={!connected || pendingDevices.includes('camera')}
                onClick={() => void toggle('camera')}>
                {devices.camera ? <Video aria-hidden="true" size={16} /> : <VideoOff aria-hidden="true" size={16} />}
                {devices.camera ? 'Turn off camera' : 'Turn on camera'}
              </Button>
              {sharingAvailable && <Button aria-pressed={devices.screen} disabled={!connected || pendingDevices.includes('screen')}
                onClick={() => void toggle('screen')}>
                <MonitorUp aria-hidden="true" size={16} />{devices.screen ? 'Stop sharing' : 'Share screen'}
              </Button>}
            </>
          )}
          {canRetry && <Button onClick={() => setAttempt(previous => previous + 1)}>Try again</Button>}
          {canManage && <Button onClick={() => { setConfirmEnd(true); setRemoveTarget(null); }} disabled={busy}>End meeting</Button>}
          <Button variant="danger" disabled={busy} onClick={() => void closeRoom()}>
            <PhoneOff size={16} aria-hidden="true" />{isDirect ? 'End call' : status === 'closed' || status === 'failed' ? 'Close meeting' : 'Leave meeting'}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
