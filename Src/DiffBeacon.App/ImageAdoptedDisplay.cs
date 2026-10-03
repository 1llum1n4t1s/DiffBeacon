namespace DiffBeacon.App;

// 配列は描画ownerが所有する。後続候補はcloneし、採用済みframeを変更しない。
internal sealed record ImageAdoptedDisplay(long Generation, long Revision,
    IReadOnlyList<ImageComparisonEngine.DecodedFrame> Frames, ImageDisplaySample Sample,
    ImageOverlayRenderer.Settings Settings);
