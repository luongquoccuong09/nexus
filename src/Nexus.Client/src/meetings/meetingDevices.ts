export interface MeetingDevices {
  microphone: boolean;
  camera: boolean;
  microphoneId?: string;
  cameraId?: string;
  speakerId?: string;
}

export function selectedCapture(deviceId?: string) {
  return deviceId ? { deviceId: { exact: deviceId } } : undefined;
}
