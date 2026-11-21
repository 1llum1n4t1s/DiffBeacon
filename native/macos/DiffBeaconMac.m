#import <Foundation/Foundation.h>
#import <AppKit/AppKit.h>
#import <Vision/Vision.h>
#include <stdio.h>

typedef void (*DiffBeaconPathsCallback)(const char *paths);

static DiffBeaconPathsCallback g_paths;

/// Finder のサービスが渡すファイルパスを、改行区切りで C# へ返す。
@interface DiffBeaconService : NSObject
- (void)diffBeaconCompare:(NSPasteboard *)pboard userData:(NSString *)userData error:(NSString **)error;
@end

@implementation DiffBeaconService
- (void)diffBeaconCompare:(NSPasteboard *)pboard userData:(NSString *)userData error:(NSString **)error
{
    NSArray *files = [pboard propertyListForType:NSFilenamesPboardType];
    if (files.count == 0 || g_paths == NULL) return;
    g_paths([[files componentsJoinedByString:@"\n"] UTF8String]);
}
@end

/// NSApplication ができたあとに呼ぶ。NSApp が無いときは何もしない。
void db_install_services(DiffBeaconPathsCallback callback)
{
    g_paths = callback;
    if (NSApp == nil) return;
    [NSApp setServicesProvider:[[DiffBeaconService alloc] init]];
    NSUpdateDynamicServices();
}

/// Apple Vision で画像の文字を読む。0 が成功。結果は out へ、失敗理由は error へ書く。
int db_ocr_file(const char *path, char *out, int outLen, char *error, int errorLen)
{
    @autoreleasepool {
        if (path == NULL || out == NULL || outLen < 2) return 1;
        out[0] = 0;
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        VNRecognizeTextRequest *request = [[VNRecognizeTextRequest alloc] init];
        request.recognitionLevel = VNRequestTextRecognitionLevelAccurate;
        NSError *err = nil;
        VNImageRequestHandler *handler = [[VNImageRequestHandler alloc] initWithURL:url options:@{}];
        if (![handler performRequests:@[request] error:&err])
        {
            if (error != NULL && errorLen > 0)
                snprintf(error, (size_t)errorLen, "%s", err.localizedDescription.UTF8String ?: "vision");
            return 2;
        }
        NSMutableString *text = [NSMutableString string];
        for (id item in request.results)
        {
            if (![item isKindOfClass:[VNRecognizedTextObservation class]]) continue;
            VNRecognizedText *best = [[(VNRecognizedTextObservation *)item topCandidates:1] firstObject];
            if (best != nil)
            {
                [text appendString:best.string];
                [text appendString:@"\n"];
            }
        }
        snprintf(out, (size_t)outLen, "%s", text.UTF8String ?: "");
        return 0;
    }
}
