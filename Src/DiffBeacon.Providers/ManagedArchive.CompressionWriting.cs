using SharpCompress.Compressors.BZip2;

namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    private void WriteCompressionOutput(Stream output, string format,
        Action<Action<string, Stream?, DateTime?>> populate, CancellationToken token)
    {
        Stream compression = format switch
        {
            "bzip2" => BZip2Stream.Create(output, SharpCompress.Compressors.CompressionMode.Compress,
                false, leaveOpen: true),
            "Z" => new ZWriteStream(output, token, leaveOpen: true),
            _ => throw new InvalidDataException("単一ファイルの圧縮出力形式が不正です。")
        };
        try
        {
            var count = 0;
            populate((path, content, time) =>
            {
                token.ThrowIfCancellationRequested();
                if (content is null || count != 0)
                    throw new InvalidDataException("BZip2/Z出力にはディレクトリを含まない単一ファイルが必要です。");
                count++;
                // 格納名は保存しないが、通常のentryと同じ安全性検査を維持する。
                // 復号時のleaf名は出力basenameから決まり、更新日時も形式には格納されない。
                _ = ValidateEntryPath(path);
                long length = 0;
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = content.Read(buffer);
                    if (read == 0) break;
                    if (read > _limits.MaximumEntryBytes - length || read > _limits.MaximumDecodedBytes - length)
                        throw new InvalidDataException("アーカイブ内容のサイズ上限を超えました。");
                    length += read;
                    compression.Write(buffer, 0, read);
                }
            });
            // RepackのReadが形式に応じた全入力の検証を終えた後だけ明示完了する。
            token.ThrowIfCancellationRequested();
            if (count != 1) throw new InvalidDataException("BZip2/Z出力には単一ファイルが必要です。");
            if (compression is BZip2Stream bzip2) bzip2.Finish();
            else if (compression is ZWriteStream z) z.Complete();
        }
        catch (Exception primary)
        {
            try { compression.Dispose(); }
            catch (Exception cleanup)
            {
                // CLI/GUIはMessageを表示するため、両理由をMessageにも含むAggregateExceptionを使う。
                var failures = new AggregateException("BZip2/Z書込みの失敗と圧縮ストリームの終了処理失敗。", primary, cleanup);
                // 取消の分類とtokenを維持し、両例外はinner aggregateへ残す。
                if (primary is OperationCanceledException canceled)
                    throw new OperationCanceledException(failures.Message, failures, canceled.CancellationToken);
                throw failures;
            }
            throw;
        }
        // 正常経路のDispose失敗も呼出し元へ伝え、SaveOutputの公開を止める。
        compression.Dispose();
        // BZip2のDisposeは例外経路でも内部Finishを呼ぶが、SaveOutputは失敗した一時出力を公開しない。
        // ZのDispose/Flushは未完了codeを出力しない。
    }
}
