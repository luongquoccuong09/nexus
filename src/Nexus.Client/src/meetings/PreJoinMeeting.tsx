import { useCallback, useEffect, useRef, useState } from 'react';
import { VideoOff } from 'lucide-react';
import { Button, Field, Modal } from '../ui';
import type { MeetingDevices } from './meetingDevices';
import { permissionStatus, previewDeviceOptions } from './preJoinCapture';
import { usePreJoinCapture } from './usePreJoinCapture';
import { useSpeakerTest } from './useSpeakerTest';
import './prejoin.css';

function CameraPreview({ stream }: { stream: MediaStream | null }) {
  const video = useRef<HTMLVideoElement>(null);
  useEffect(() => {
    const element = video.current;
    if (!element || !stream) return;
    element.srcObject = stream;
    return () => { element.srcObject = null; };
  }, [stream]);

  return (
    <div className="nx-prejoin-video">
      {stream ? <video ref={video} autoPlay playsInline muted aria-label="Camera preview" /> : (
        <div><VideoOff size={26} aria-hidden="true" /><span>Camera is off</span></div>
      )}
    </div>
  );
}

function DeviceSelect({ label, options, value, disabled, onChange }: {
  label: 'Camera' | 'Microphone' | 'Speaker';
  options: { id: string; label: string }[];
  value: string;
  disabled: boolean;
  onChange: (id: string) => void;
}) {
  const id = `prejoin-${label.toLowerCase()}`;
  return (
    <Field label={label} htmlFor={id} compact>
      <select id={id} value={value} disabled={disabled} onChange={event => onChange(event.currentTarget.value)}>
        <option value="">System default</option>
        {options.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
      </select>
    </Field>
  );
}

export default function PreJoinMeeting({ title, onJoin, onClose }: {
  title: string;
  onJoin: (devices: MeetingDevices) => void;
  onClose: () => void;
}) {
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([]);
  const [enumerationFailed, setEnumerationFailed] = useState(false);
  const enumeration = useRef(0);
  const [cameraId, setCameraId] = useState('');
  const [microphoneId, setMicrophoneId] = useState('');
  const [speakerId, setSpeakerId] = useState('');
  const refreshDevices = useCallback(() => {
    const version = ++enumeration.current;
    void navigator.mediaDevices?.enumerateDevices().then(result => {
      if (version === enumeration.current) {
        setDevices(result);
        setEnumerationFailed(false);
      }
    }).catch(() => { if (version === enumeration.current) setEnumerationFailed(true); });
  }, []);

  useEffect(() => {
    const media = navigator.mediaDevices;
    refreshDevices();
    media?.addEventListener('devicechange', refreshDevices);
    return () => {
      enumeration.current++;
      media?.removeEventListener('devicechange', refreshDevices);
    };
  }, [refreshDevices]);

  const camera = usePreJoinCapture('camera', refreshDevices);
  const microphone = usePreJoinCapture('microphone', refreshDevices);
  const speaker = useSpeakerTest();
  const cameraOptions = previewDeviceOptions(devices, 'videoinput');
  const microphoneOptions = previewDeviceOptions(devices, 'audioinput');
  const speakerOptions = speaker.canSelect ? previewDeviceOptions(devices, 'audiooutput') : [];
  const selectedCamera = cameraOptions.some(option => option.id === cameraId) ? cameraId : '';
  const selectedMicrophone = microphoneOptions.some(option => option.id === microphoneId) ? microphoneId : '';
  const selectedSpeaker = speakerOptions.some(option => option.id === speakerId) ? speakerId : '';
  const captureAvailable = Boolean(navigator.mediaDevices?.getUserMedia);

  function releaseDevices() {
    camera.release();
    microphone.release();
    speaker.release();
  }

  function close() {
    releaseDevices();
    onClose();
  }

  function join() {
    const selection: MeetingDevices = {
      microphone: microphone.stream?.getAudioTracks().some(track => track.readyState === 'live') ?? false,
      camera: camera.stream?.getVideoTracks().some(track => track.readyState === 'live') ?? false,
      microphoneId: selectedMicrophone || undefined,
      cameraId: selectedCamera || undefined,
      speakerId: selectedSpeaker || undefined
    };
    releaseDevices();
    onJoin(selection);
  }

  return (
    <Modal open size="xl" title="Pre-join" description={title}
      onOpenChange={open => { if (!open) close(); }}
      footer={<><Button onClick={close}>Back</Button><Button variant="primary" onClick={join}>Join meeting</Button></>}>
      <div className="nx-prejoin">
        <CameraPreview stream={camera.stream} />
        <div className="nx-prejoin-controls">
          {!captureAvailable && <p className="nx-muted">Device previews require a secure connection in a supported browser.</p>}
          {enumerationFailed && <p className="nx-muted">Unable to list devices. You can still try the system defaults.</p>}
          <div className="nx-prejoin-device">
            <DeviceSelect label="Camera" options={cameraOptions} value={selectedCamera} disabled={!captureAvailable || camera.loading}
              onChange={id => {
                setCameraId(id);
                if (camera.stream) void camera.start(id);
                else camera.stop();
              }} />
            <Button size="sm" disabled={!captureAvailable || camera.loading}
              onClick={() => camera.stream ? camera.stop() : void camera.start(selectedCamera)}>
              {camera.stream ? 'Stop camera preview' : 'Start camera preview'}
            </Button>
            {captureAvailable && !camera.error && <p className="nx-muted">{permissionStatus('camera', camera.permission)}</p>}
            {camera.loading && <p className="nx-muted" role="status">Starting camera preview…</p>}
            {camera.error && <p className="nx-error" role="alert">{camera.error}</p>}
          </div>
          <div className="nx-prejoin-device">
            <DeviceSelect label="Microphone" options={microphoneOptions} value={selectedMicrophone}
              disabled={!captureAvailable || microphone.loading} onChange={id => {
                setMicrophoneId(id);
                if (microphone.stream) void microphone.start(id);
                else microphone.stop();
              }} />
            <Button size="sm" disabled={!captureAvailable || microphone.loading}
              onClick={() => microphone.stream ? microphone.stop() : void microphone.start(selectedMicrophone)}>
              {microphone.stream ? 'Stop microphone test' : 'Test microphone'}
            </Button>
            <div className="nx-prejoin-meter" role="meter" aria-label="Microphone level"
              aria-valuemin={0} aria-valuemax={100} aria-valuenow={microphone.level}>
              <span style={{ width: `${microphone.level}%` }} />
            </div>
            {captureAvailable && !microphone.error &&
              <p className="nx-muted">{permissionStatus('microphone', microphone.permission)}</p>}
            {microphone.loading && <p className="nx-muted" role="status">Starting microphone test…</p>}
            {microphone.meterUnavailable && <p className="nx-muted">Microphone level is unavailable in this browser.</p>}
            {microphone.error && <p className="nx-error" role="alert">{microphone.error}</p>}
          </div>
          <div className="nx-prejoin-device">
            <DeviceSelect label="Speaker" options={speakerOptions} value={selectedSpeaker}
              disabled={!speaker.canSelect || speakerOptions.length === 0} onChange={id => { setSpeakerId(id); speaker.reset(); }} />
            <Button size="sm" disabled={!speaker.supported || speaker.status === 'playing'}
              onClick={() => void speaker.play(selectedSpeaker)}>Test speaker</Button>
            {!speaker.canSelect && <p className="nx-muted">This browser uses the system default speaker.</p>}
            {speaker.canSelect && speakerOptions.length === 0 &&
              <p className="nx-muted">Other speakers may appear after you allow microphone access.</p>}
            {!speaker.supported && <p className="nx-muted">Speaker testing is unavailable in this browser.</p>}
            {speaker.status === 'playing' && <p className="nx-muted" role="status">Playing test tone…</p>}
            {speaker.status === 'complete' && <p className="nx-muted" role="status">Speaker test complete.</p>}
            {speaker.error && <p className="nx-error" role="alert">{speaker.error}</p>}
          </div>
        </div>
      </div>
    </Modal>
  );
}
