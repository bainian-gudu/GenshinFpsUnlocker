//patch.h
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
 included in all copies of the Software.
 
 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 OTHER DEALINGS IN THE SOFTWARE.
*/

#ifndef HPatch_patch_h
#define HPatch_patch_h
#include "patch_types.h"
#ifdef __cplusplus
extern "C" {
#endif

//全部 补丁*() functions do 不 allocate 内存

//optimize speed 用于 patch_stream_with_cache() && patch_decompress_with_cache():
//  preload 部分 的 旧数据 到 缓存,
//  缓存 内存 大小 (temp_cache_end-temp_cache) the larger the better 用于 large 旧数据 文件
#ifndef _IS_NEED_CACHE_OLD_BY_COVERS
#   define _IS_NEED_CACHE_OLD_BY_COVERS 1
#endif


//generate 新数据 通过 补丁(旧数据 + serializedDiff)
//  serializedDiff 创建 通过 create_diff()
hpatch_BOOL patch(unsigned char* out_newData,unsigned char* out_newData_end,
                  const unsigned char* oldData,const unsigned char* oldData_end,
                  const unsigned char* serializedDiff,const unsigned char* serializedDiff_end);

//补丁 通过 流, see 补丁()
//  已使用 (hpatch_kStreamCacheSize*8 stack 内存) 用于 I/O 缓存
//  如果 使用 patch_stream_with_cache(), 可以 passing larger 内存 缓存 到 optimize speed
//  serializedDiff 创建 通过 create_diff()
hpatch_BOOL patch_stream(const hpatch_TStreamOutput* out_newData,       //sequential 写入
                         const hpatch_TStreamInput*  oldData,           //random 读取
                         const hpatch_TStreamInput*  serializedDiff);   //random 读取

//第三方实现细节。
//  可以 passing more 内存 用于 I/O 缓存 到 optimize speed
//  注意: (temp_cache_end-temp_cache)>=2048
hpatch_BOOL patch_stream_with_cache(const hpatch_TStreamOutput* out_newData,    //sequential 写入
                                    const hpatch_TStreamInput*  oldData,        //random 读取
                                    const hpatch_TStreamInput*  serializedDiff, //random 读取
                                    unsigned char* temp_cache,unsigned char* temp_cache_end);




//第三方实现细节。
//  compressedDiff created 通过 create_compressed_diff() 或 create_compressed_diff_stream()
hpatch_BOOL getCompressedDiffInfo(hpatch_compressedDiffInfo* out_diffInfo,
                                  const hpatch_TStreamInput* compressedDiff);
//第三方实现细节。
hpatch_inline static hpatch_BOOL
    getCompressedDiffInfo_mem(hpatch_compressedDiffInfo* out_diffInfo,
                              const unsigned char* compressedDiff,
                              const unsigned char* compressedDiff_end){
        hpatch_TStreamInput  diffStream;
        mem_as_hStreamInput(&diffStream,compressedDiff,compressedDiff_end);
        return getCompressedDiffInfo(out_diffInfo,&diffStream);
    }
    
//补丁 含有 解压 插件
//  已使用 (hpatch_kStreamCacheSize*6 stack 内存) + (解压 缓冲区*4)
//  compressedDiff 创建 通过 create_compressed_diff() 或 create_compressed_diff_stream()
//  decompressPlugin 可以 null 当 no compressed 数据 在 compressedDiff
//  如果 使用 patch_decompress_with_cache(), 可以 passing larger 内存 缓存 到 optimize speed
hpatch_BOOL patch_decompress(const hpatch_TStreamOutput* out_newData,       //sequential 写入
                             const hpatch_TStreamInput*  oldData,           //random 读取
                             const hpatch_TStreamInput*  compressedDiff,    //random 读取
                             hpatch_TDecompress* decompressPlugin);

//第三方实现细节。
//  可以 passing larger 内存 缓存 到 optimize speed
//  注意: (temp_cache_end-temp_cache)>=2048
hpatch_BOOL patch_decompress_with_cache(const hpatch_TStreamOutput* out_newData,    //sequential 写入
                                        const hpatch_TStreamInput*  oldData,        //random 读取
                                        const hpatch_TStreamInput*  compressedDiff, //random 读取
                                        hpatch_TDecompress* decompressPlugin,
                                        unsigned char* temp_cache,unsigned char* temp_cache_end);

//第三方实现细节。
hpatch_inline static hpatch_BOOL
    patch_decompress_mem(unsigned char* out_newData,unsigned char* out_newData_end,
                         const unsigned char* oldData,const unsigned char* oldData_end,
                         const unsigned char* compressedDiff,const unsigned char* compressedDiff_end,
                         hpatch_TDecompress* decompressPlugin){
        hpatch_TStreamOutput out_newStream;
        hpatch_TStreamInput  oldStream;
        hpatch_TStreamInput  diffStream;
        mem_as_hStreamOutput(&out_newStream,out_newData,out_newData_end);
        mem_as_hStreamInput(&oldStream,oldData,oldData_end);
        mem_as_hStreamInput(&diffStream,compressedDiff,compressedDiff_end);
        return patch_decompress(&out_newStream,&oldStream,&diffStream,decompressPlugin);
    }




// hpatch_TCoverList: 打开 diffData 和 读取 coverList
    typedef struct hpatch_TCoverList{
        hpatch_TCovers* ICovers;
    //private:
        unsigned char _buf[hpatch_kStreamCacheSize*4];
    } hpatch_TCoverList;

hpatch_inline static
void        hpatch_coverList_init(hpatch_TCoverList* coverList) {
                                  assert(coverList!=0); memset(coverList,0,sizeof(*coverList)-sizeof(coverList->_buf)); }
//  serializedDiff 创建 通过 create_diff()
hpatch_BOOL hpatch_coverList_open_serializedDiff(hpatch_TCoverList*         out_coverList,
                                                 const hpatch_TStreamInput* serializedDiff);
//  compressedDiff 创建 通过 create_compressed_diff() 或 create_compressed_diff_stream()
hpatch_BOOL hpatch_coverList_open_compressedDiff(hpatch_TCoverList*         out_coverList,
                                                 const hpatch_TStreamInput* compressedDiff,
                                                 hpatch_TDecompress*        decompressPlugin);
hpatch_inline static
hpatch_BOOL hpatch_coverList_close(hpatch_TCoverList* coverList) {
                                   hpatch_BOOL result=hpatch_TRUE;
                                   if ((coverList!=0)&&(coverList->ICovers)){
                                       result=coverList->ICovers->close(coverList->ICovers);
                                       hpatch_coverList_init(coverList); } return result; }




//补丁 singleCompressedDiff 含有 listener
//	已使用 (stepMemSize 内存) + (I/O 缓存 内存) + (解压 缓冲区*1)
//  every byte 在 singleCompressedDiff will 仅 为 读取 once 在 order
//  singleCompressedDiff 创建 通过 create_single_compressed_diff() 或 create_single_compressed_diff_stream()
//  you 可以 download&补丁 diffData at the same time, 不含 saving it 到 disk
//  same 作为 call getSingleCompressedDiffInfo() + listener->onDiffInfo() + patch_single_compressed_diff()
hpatch_BOOL patch_single_stream(sspatch_listener_t* listener, //call back 当 got diffInfo
                                const hpatch_TStreamOutput* out_newData,          //sequential 写入
                                const hpatch_TStreamInput*  oldData,              //random 读取
                                const hpatch_TStreamInput*  singleCompressedDiff, //sequential 读取 every byte
                                hpatch_StreamPos_t  diffInfo_pos, //默认 0, 开始 pos 在 singleCompressedDiff
                                sspatch_coversListener_t* coversListener //默认 NULL, call 通过 在 got covers
                                );
static hpatch_inline hpatch_BOOL
    patch_single_stream_mem(sspatch_listener_t* listener,
                            unsigned char* out_newData,unsigned char* out_newData_end,
                            const unsigned char* oldData,const unsigned char* oldData_end,
                            const unsigned char* diff,const unsigned char* diff_end,
                            sspatch_coversListener_t* coversListener){
        hpatch_TStreamOutput out_newStream;
        hpatch_TStreamInput  oldStream;
        hpatch_TStreamInput  diffStream;
        mem_as_hStreamOutput(&out_newStream,out_newData,out_newData_end);
        mem_as_hStreamInput(&oldStream,oldData,oldData_end);
        mem_as_hStreamInput(&diffStream,diff,diff_end);
        return patch_single_stream(listener,&out_newStream,&oldStream,&diffStream,0,coversListener);
    }

//第三方实现细节。
//  singleCompressedDiff 创建 通过 create_single_compressed_diff() 或 create_single_compressed_diff_stream()
hpatch_BOOL getSingleCompressedDiffInfo(hpatch_singleCompressedDiffInfo* out_diffInfo,
                                        const hpatch_TStreamInput*  singleCompressedDiff,   //顺序读取
                                        hpatch_StreamPos_t diffInfo_pos//默认 0, 开始 pos 在 singleCompressedDiff
                                        );
hpatch_inline static hpatch_BOOL
    getSingleCompressedDiffInfo_mem(hpatch_singleCompressedDiffInfo* out_diffInfo,
                                    const unsigned char* singleCompressedDiff,
                                    const unsigned char* singleCompressedDiff_end){
        hpatch_TStreamInput  diffStream;
        mem_as_hStreamInput(&diffStream,singleCompressedDiff,singleCompressedDiff_end);
        return getSingleCompressedDiffInfo(out_diffInfo,&diffStream,0);            
    }

//补丁 singleCompressedDiff 含有 diffInfo
//	已使用 (stepMemSize 内存) + (I/O 缓存 内存) + (解压 缓冲区*1)
//	注意: (I/O 缓存 内存) >= hpatch_kStreamCacheSize*3
//  temp_cache_end-temp_cache == stepMemSize + (I/O 缓存 内存)
//  singleCompressedDiff 创建 通过 create_single_compressed_diff() 或 create_single_compressed_diff_stream()
//  decompressPlugin 可以 null 当 no compressed 数据 在 singleCompressedDiff
//  same 作为 call compressed_stream_as_uncompressed() + patch_single_stream_diff()
hpatch_BOOL patch_single_compressed_diff(const hpatch_TStreamOutput* out_newData,          //sequential 写入
                                         const hpatch_TStreamInput*  oldData,              //random 读取
                                         const hpatch_TStreamInput*  singleCompressedDiff, //顺序读取
                                         hpatch_StreamPos_t          diffData_pos, //diffData 开始 pos 在 singleCompressedDiff
                                         hpatch_StreamPos_t          uncompressedSize,
                                         hpatch_StreamPos_t          compressedSize,
                                         hpatch_TDecompress*         decompressPlugin,
                                         hpatch_StreamPos_t coverCount,hpatch_size_t stepMemSize,
                                         unsigned char* temp_cache,unsigned char* temp_cache_end,
                                         sspatch_coversListener_t* coversListener //默认 NULL, call 通过 在 got covers
                                         );

hpatch_BOOL compressed_stream_as_uncompressed(hpatch_TUncompresser_t* uncompressedStream,hpatch_StreamPos_t uncompressedSize,
                                                hpatch_TDecompress* decompressPlugin,const hpatch_TStreamInput* compressedStream,
                                                hpatch_StreamPos_t compressed_pos,hpatch_StreamPos_t compressed_end);
void close_compressed_stream_as_uncompressed(hpatch_TUncompresser_t* uncompressedStream);

hpatch_BOOL patch_single_stream_diff(const hpatch_TStreamOutput*  out_newData,          //sequential 写入
                                     const hpatch_TStreamInput*   oldData,              //random 读取
                                     const hpatch_TStreamInput*   uncompressedDiffData, //顺序读取
                                     hpatch_StreamPos_t           diffData_pos,//diffData 开始 pos 在 uncompressedDiffData
                                     hpatch_StreamPos_t           diffData_posEnd,//diffData 结束 pos 在 uncompressedDiffData
                                     hpatch_StreamPos_t coverCount,hpatch_size_t stepMemSize,
                                     unsigned char* temp_cache,unsigned char* temp_cache_end,
                                     sspatch_coversListener_t* coversListener);

#ifdef __cplusplus
}
#endif

#endif
