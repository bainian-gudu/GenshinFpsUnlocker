//diff.h
//
/*
 The MIT License (MIT)
 Copyright (c) 2012-2018 HouSisong
 
 Permission is hereby granted, free of charge, to any person
 obtaining a copy of this software and associated documentation
 files (the "Software"), to deal in the Software without
 restriction, including without limitation the rights to use,
 copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the
 Software is furnished to do so, subject to the following
 conditions:
 
 The above copyright notice and this permission notice shall be
 included in all copies or substantial portions of the Software.
 
 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 OTHER DEALINGS IN THE SOFTWARE.
*/

#ifndef HDiff_diff_h
#define HDiff_diff_h
#include <vector>
#include "diff_types.h"

static const int kMinSingleMatchScore_default = 6;

//创建 oldData 与 newData 之间的差异数据
//  out_diff 是 uncompressed, you 可以 使用 create_compressed_diff()
//       或 create_single_compressed_diff() 创建 compressed 差异 数据
//  recommended always 使用 create_single_compressed_diff() replace create_diff()
//  kMinSingleMatchScore: 默认 6, bin: 0--4  text: 4--9
//  isUseBigCacheMatch: big 缓存 max 已使用 O(oldSize) 内存, 匹配 speed faster, but build big 缓存 slow
void create_diff(const unsigned char* newData,const unsigned char* newData_end,
                 const unsigned char* oldData,const unsigned char* oldData_end,
                 std::vector<unsigned char>& out_diff,
                 int kMinSingleMatchScore=kMinSingleMatchScore_default,
                 bool isUseBigCacheMatch=false,size_t threadNum=1);

//返回 补丁(旧数据+差异)==新数据?
bool check_diff(const unsigned char* newData,const unsigned char* newData_end,
                const unsigned char* oldData,const unsigned char* oldData_end,
                const unsigned char* diff,const unsigned char* diff_end);
bool check_diff(const hpatch_TStreamInput*  newData,
                const hpatch_TStreamInput*  oldData,
                const hpatch_TStreamInput*  diff);




//创建 a compressed 差异 数据 between 旧数据 和 新数据
//  out_diff compressed 通过 compressPlugin
//  recommended always 使用 create_single_compressed_diff() replace create_compressed_diff()
//  kMinSingleMatchScore: 默认 6, bin: 0--4  text: 4--9
//  isUseBigCacheMatch: big 缓存 max 已使用 O(oldSize) 内存, 匹配 speed faster, but build big 缓存 slow
void create_compressed_diff(const unsigned char* newData,const unsigned char* newData_end,
                            const unsigned char* oldData,const unsigned char* oldData_end,
                            std::vector<unsigned char>& out_diff,
                            const hdiff_TCompress* compressPlugin=0,
                            int kMinSingleMatchScore=kMinSingleMatchScore_default,
                            bool isUseBigCacheMatch=false,
                            ICoverLinesListener* listener=0,size_t threadNum=1);
void create_compressed_diff(const unsigned char* newData,const unsigned char* newData_end,
                            const unsigned char* oldData,const unsigned char* oldData_end,
                            const hpatch_TStreamOutput* out_diff,
                            const hdiff_TCompress* compressPlugin=0,
                            int kMinSingleMatchScore=kMinSingleMatchScore_default,
                            bool isUseBigCacheMatch=false,
                            ICoverLinesListener* listener=0,size_t threadNum=1);

//创建 a compressed 差异 数据 通过 流:
//  可以 control 内存 requires 和 run speed 通过 different kMatchBlockSize 值,
//      but out_diff 大小 是 larger than create_compressed_diff()
//  recommended 已使用 在 limited environment 或 支持 large 文件
//  recommended always 使用 create_single_compressed_diff_stream() replace create_compressed_diff_stream()
//  第三方实现细节。
//    如果 increase kMatchBlockSize 然后 run faster 和 require less 内存, but out_diff 大小 increase
//  注意: out_diff->写入()'s writeToPos may 为 back 到 update headData!
//  throw std::runtime_error 当 I/O 错误,etc.
static const size_t kMatchBlockSize_default = (1<<6);
static const size_t kMatchBlockSize_min=4;
void create_compressed_diff_stream(const hpatch_TStreamInput*  newData,
                                   const hpatch_TStreamInput*  oldData,
                                   const hpatch_TStreamOutput* out_diff,
                                   const hdiff_TCompress* compressPlugin=0,
                                   size_t kMatchBlockSize=kMatchBlockSize_default,
                                   const hdiff_TMTSets_s* mtsets=0);

//返回 patch_decompress(旧数据+差异)==新数据?
bool check_compressed_diff(const unsigned char* newData,const unsigned char* newData_end,
                           const unsigned char* oldData,const unsigned char* oldData_end,
                           const unsigned char* diff,const unsigned char* diff_end,
                           hpatch_TDecompress* decompressPlugin);
bool check_compressed_diff(const hpatch_TStreamInput*  newData,
                           const hpatch_TStreamInput*  oldData,
                           const hpatch_TStreamInput*  compressed_diff,
                           hpatch_TDecompress* decompressPlugin);
// check_compressed_diff_stream rename 到 check_compressed_diff

//重新保存 compressed_diff
//  解压 in_diff 并重新压缩到 out_diff
//  throw std::runtime_error 当 输入 文件 错误 或 I/O 错误,etc.
void resave_compressed_diff(const hpatch_TStreamInput*  in_diff,
                            hpatch_TDecompress*         decompressPlugin,
                            const hpatch_TStreamOutput* out_diff,
                            const hdiff_TCompress*      compressPlugin,
                            hpatch_StreamPos_t          out_diff_curPos=0);




static const size_t kDefaultPatchStepMemSize =1024*256;

//创建 a 差异 数据 between 旧数据 和 新数据, the diffData saved 作为 单个 compressed 流
//  kMinSingleMatchScore: 默认 6, bin: 0--4  text: 4--9
//  patchStepMemSize>=hpatch_kStreamCacheSize, 默认 256k, recommended 64k,2m etc...
//  isUseBigCacheMatch: big 缓存 max 已使用 O(oldSize) 内存, 匹配 speed faster, but build big 缓存 slow
void create_single_compressed_diff(const unsigned char* newData,const unsigned char* newData_end,
                                   const unsigned char* oldData,const unsigned char* oldData_end,
                                   std::vector<unsigned char>& out_diff,const hdiff_TCompress* compressPlugin=0,
                                   int kMinSingleMatchScore=kMinSingleMatchScore_default,
                                   size_t patchStepMemSize=kDefaultPatchStepMemSize,
                                   bool isUseBigCacheMatch=false,
                                   ICoverLinesListener* listener=0,size_t threadNum=1);
extern "C" void create_single_compressed_diff(const unsigned char* newData,const unsigned char* newData_end,
                                   const unsigned char* oldData,const unsigned char* oldData_end,
                                   const hpatch_TStreamOutput* out_diff,const hdiff_TCompress* compressPlugin=0,
                                   int kMinSingleMatchScore=kMinSingleMatchScore_default,
                                   size_t patchStepMemSize=kDefaultPatchStepMemSize,
                                   bool isUseBigCacheMatch=false,
                                   ICoverLinesListener* listener=0,size_t threadNum=1);
//创建 单个 compressed 差异 数据 通过 流:
//  可以 control 内存 requires 和 run speed 通过 different kMatchBlockSize 值,
//      but out_diff 大小 是 larger than create_single_compressed_diff()
//  recommended 已使用 在 limited environment 或 支持 large 文件
//  第三方实现细节。
//    如果 increase kMatchBlockSize 然后 run faster 和 require less 内存, but out_diff 大小 increase
//  注意: out_diff->写入()'s writeToPos may 为 back 到 update headData!
//  throw std::runtime_error 当 I/O 错误,etc.
void create_single_compressed_diff_stream(const hpatch_TStreamInput*  newData,
                                          const hpatch_TStreamInput*  oldData,
                                          const hpatch_TStreamOutput* out_diff,
                                          const hdiff_TCompress* compressPlugin=0,
                                          size_t kMatchBlockSize=kMatchBlockSize_default,
                                          size_t patchStepMemSize=kDefaultPatchStepMemSize,
                                          const hdiff_TMTSets_s* mtsets=0);

//返回 patch_single_?(旧数据+差异)==新数据?
bool check_single_compressed_diff(const unsigned char* newData,const unsigned char* newData_end,
                                  const unsigned char* oldData,const unsigned char* oldData_end,
                                  const unsigned char* diff,const unsigned char* diff_end,
                                  hpatch_TDecompress* decompressPlugin);
bool check_single_compressed_diff(const hpatch_TStreamInput* newData,
                                  const hpatch_TStreamInput* oldData,
                                  const hpatch_TStreamInput* diff,
                                  hpatch_TDecompress* decompressPlugin);

//第三方实现细节。
//  解压 in_diff 并重新压缩到 out_diff
//  throw std::runtime_error 当 输入 文件 错误 或 I/O 错误,etc.
//  返回 新 out_diff curPos
hpatch_StreamPos_t
     resave_single_compressed_diff(const hpatch_TStreamInput*  in_diff,
                                   hpatch_TDecompress*         decompressPlugin,
                                   const hpatch_TStreamOutput* out_diff,
                                   const hdiff_TCompress*      compressPlugin,
                                   const hpatch_singleCompressedDiffInfo* diffInfo=0,
                                   hpatch_StreamPos_t          in_diff_curPos=0,
                                   hpatch_StreamPos_t          out_diff_curPos=0);


//same 作为 创建?compressed_diff_stream(), but 不 serialize diffData, 仅 got covers
void get_match_covers_by_block(const hpatch_TStreamInput* newData,const hpatch_TStreamInput* oldData,
                               hpatch_TOutputCovers* out_covers,size_t kMatchBlockSize,const hdiff_TMTSets_s* mtsets);
void get_match_covers_by_block(const unsigned char* newData,const unsigned char* newData_end,
                               const unsigned char* oldData,const unsigned char* oldData_end,
                               hpatch_TOutputCovers* out_covers,size_t kMatchBlockSize,size_t threadNum);

//same 作为 创建?_diff(), but 不 serialize diffData, 仅 got covers
void get_match_covers_by_sstring(const unsigned char* newData,const unsigned char* newData_end,
                                 const unsigned char* oldData,const unsigned char* oldData_end,
                                 hpatch_TOutputCovers* out_covers,
                                 int kMinSingleMatchScore=kMinSingleMatchScore_default,
                                 bool isUseBigCacheMatch=false,ICoverLinesListener* listener=0,
                                 size_t threadNum=1,bool isCanExtendCover=true);
void get_match_covers_by_sstring(const unsigned char* newData,const unsigned char* newData_end,
                                 const unsigned char* oldData,const unsigned char* oldData_end,
                                 std::vector<hpatch_TCover_sz>& out_covers,
                                 int kMinSingleMatchScore=kMinSingleMatchScore_default,
                                 bool isUseBigCacheMatch=false,ICoverLinesListener* listener=0,
                                 size_t threadNum=1,bool isCanExtendCover=true);
#endif
