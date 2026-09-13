#include "../hpatch-sys/HPatch/patch_types.h"
#include <stdbool.h>

typedef hpatch_TStreamOutput hdiff_TStreamOutput;
typedef hpatch_TStreamInput hdiff_TStreamInput;
// 压缩插件
typedef struct hdiff_TCompress {
    // 返回 类型 tag; strlen(结果)<=hpatch_kMaxPluginTypeLength; (注意:结果 lifetime)
    const char *(*compressType)(void); // ascii cstring,不能 contain '&'
    // 返回 the max compressed 大小, 如果 输入 dataSize 数据;
    hpatch_StreamPos_t (*maxCompressedSize)(hpatch_StreamPos_t dataSize);
    // 返回支持的线程数
    int (*setParallelThreadNumber)(struct hdiff_TCompress *compressPlugin, int threadNum);
    // 压缩 数据 到 out_code; 返回 compressed 大小, 如果 错误 或 不 需要 压缩 然后 返回 0;
    // 如果 out_code->写入() 返回 hdiff_stream_kCancelCompress(错误) 然后 返回 0;
    // 如果 内存 I/O 可以 使用 hdiff_compress_mem()
    hpatch_StreamPos_t (*compress)(const struct hdiff_TCompress *compressPlugin,
                                   const hpatch_TStreamOutput *out_code,
                                   const hpatch_TStreamInput *in_data);
    const char *(*compressTypeForDisplay)(void); // like compressType but just 用于 显示,可以 NULL
} hdiff_TCompress;

// 创建 a 差异 数据 between 旧数据 和 新数据, the diffData saved 作为 单个 compressed 流
//   kMinSingleMatchScore: 默认 6, bin: 0--4  text: 4--9
//   patchStepMemSize>=hpatch_kStreamCacheSize, 默认 256k, recommended 64k,2m etc...
//   isUseBigCacheMatch: big 缓存 max 已使用 O(oldSize) 内存, 匹配 speed faster, but build big 缓存 slow
void create_single_compressed_diff(const unsigned char *newData, const unsigned char *newData_end,
                                   const unsigned char *oldData, const unsigned char *oldData_end,
                                   const hpatch_TStreamOutput *out_diff, const hdiff_TCompress *compressPlugin,
                                   int kMinSingleMatchScore,
                                   size_t patchStepMemSize,
                                   bool isUseBigCacheMatch,
                                   void *listener, size_t threadNum);