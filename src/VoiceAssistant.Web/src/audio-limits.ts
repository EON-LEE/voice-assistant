export const audioFrameBytes = 640;
// A bounded three-second pool rides out main-thread stalls; a longer stall drops (and reports) audio instead of growing memory.
export const audioBufferCount = 150;
