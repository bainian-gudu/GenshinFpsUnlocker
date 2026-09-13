//  patch_types.h
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

#ifndef HPatch_patch_types_h
#define HPatch_patch_types_h

#include <string.h> //用于 size_t memset memcpy memmove
#include <assert.h>

#ifdef __cplusplus
extern "C" {
#endif

#define HDIFFPATCH_VERSION_MAJOR    4
#define HDIFFPATCH_VERSION_MINOR    8
#define HDIFFPATCH_VERSION_RELEASE  0

#define _HDIFFPATCH_VERSION          HDIFFPATCH_VERSION_MAJOR.HDIFFPATCH_VERSION_MINOR.HDIFFPATCH_VERSION_RELEASE
#define _HDIFFPATCH_QUOTE(str) #str
#define _HDIFFPATCH_EXPAND_AND_QUOTE(str) _HDIFFPATCH_QUOTE(str)
#define HDIFFPATCH_VERSION_STRING   _HDIFFPATCH_EXPAND_AND_QUOTE(_HDIFFPATCH_VERSION)
#define HDIFFPATCH_VERSION_NUMBER   ((HDIFFPATCH_VERSION_MAJOR*1000+HDIFFPATCH_VERSION_MINOR)*1000+HDIFFPATCH_VERSION_RELEASE)

#ifndef hpatch_int
    typedef int                 hpatch_int;
#endif
#ifndef hpatch_uint
    typedef unsigned int        hpatch_uint;
#endif
#ifndef hpatch_size_t
    typedef size_t              hpatch_size_t;
#endif
#ifndef hpatch_uint32_t
#ifdef _MSC_VER
#   if (_MSC_VER >= 1300)
    typedef unsigned __int32    hpatch_uint32_t;
#   else
    typedef unsigned int        hpatch_uint32_t;
#   endif
#else
    typedef unsigned int        hpatch_uint32_t;
#endif
#endif
#ifndef hpatch_uint64_t
#ifdef _MSC_VER
    typedef unsigned __int64    hpatch_uint64_t;
#else
    typedef unsigned long long  hpatch_uint64_t;
#endif
#endif
#ifndef hpatch_StreamPos_t
    typedef hpatch_uint64_t     hpatch_StreamPos_t; // 文件 大小 类型
#endif
#define hpatch_kNullStreamPos   (~(hpatch_StreamPos_t)0)

#ifndef hpatch_BOOL
    typedef int                 hpatch_BOOL;
#endif
#define     hpatch_FALSE    0
#define     hpatch_TRUE     ((hpatch_BOOL)(!hpatch_FALSE))
    
#ifndef hpatch_byte
    typedef unsigned char       hpatch_byte;
#endif

#if (_HPATCH_IS_USED_errno)
typedef    int          hpatch_FileError_t;// 0: no 错误; other: saved errno 值;
#else
typedef    hpatch_BOOL  hpatch_FileError_t;// 0: no 错误; other: 错误;
#endif

#ifdef _MSC_VER
#   define hpatch_inline _inline
#else
#   define hpatch_inline inline
#endif

//PRIu64 用于 printf 类型 hpatch_StreamPos_t
#ifndef PRIu64
#   ifdef _MSC_VER
#       define PRIu64 "I64u"
#   else
#       define PRIu64 "llu"
#   endif
#endif

#ifdef ANDROID
#   include <android/log.h>
#   define LOG_ERR(...) __android_log_print(ANDROID_LOG_ERROR, "hpatch", __VA_ARGS__)
#else
#   include <stdio.h>  //用于 stderr
#   define LOG_ERR(...) fprintf(stderr,__VA_ARGS__)
#endif
#ifndef _HPATCH_IS_USED_errno
#   define  _HPATCH_IS_USED_errno 1
#endif
#define _hpatch_import_system_tag "call import system api"
#if (_HPATCH_IS_USED_errno)
#   define  LOG_ERRNO(_err_no) \
        LOG_ERR(_hpatch_import_system_tag" error! errno: %d, errmsg: %s.\n",_err_no,strerror(_err_no))
#else
#   define  LOG_ERRNO(_err_no) LOG_ERR(_hpatch_import_system_tag" error!\n")
#endif
    
#define _hpatch_align_type_lower(uint_type,p,align2pow) (((uint_type)(p)) & (~(uint_type)((align2pow)-1)))
#define _hpatch_align_lower(p,align2pow) _hpatch_align_type_lower(hpatch_size_t,p,align2pow)
#define _hpatch_align_upper(p,align2pow) _hpatch_align_lower(((hpatch_size_t)(p))+((align2pow)-1),align2pow)
    
    typedef void* hpatch_TStreamInputHandle;
    typedef void* hpatch_TStreamOutputHandle;
    
    typedef struct hpatch_TStreamInput{
        void*            streamImport;
        hpatch_StreamPos_t streamSize; //流 大小,max 可读 range;
        //读取() 必须 读取 (out_data_end-out_data), otherwise 错误 返回 hpatch_FALSE
        hpatch_BOOL            (*read)(const struct hpatch_TStreamInput* stream,hpatch_StreamPos_t readFromPos,
                                       unsigned char* out_data,unsigned char* out_data_end);
        void*        _private_reserved;
    } hpatch_TStreamInput;
    
    typedef struct hpatch_TStreamOutput{
        void*            streamImport;
        hpatch_StreamPos_t streamSize; //流 大小,max writable range; 不 是 写入 pos!
        //read_writed 用于 ReadWriteIO, 可以 null!
        hpatch_BOOL     (*read_writed)(const struct hpatch_TStreamOutput* stream,hpatch_StreamPos_t readFromPos,
                                       unsigned char* out_data,unsigned char* out_data_end);
        //写入() 必须 wrote (out_data_end-out_data), otherwise 错误 返回 hpatch_FALSE
        hpatch_BOOL           (*write)(const struct hpatch_TStreamOutput* stream,hpatch_StreamPos_t writeToPos,
                                       const unsigned char* data,const unsigned char* data_end);
    } hpatch_TStreamOutput;
    
    //默认 once I/O (读取/写入) byte 大小
    #ifndef hpatch_kStreamCacheSize
    #   define hpatch_kStreamCacheSize      (1024*4)
    #endif
    #ifndef hpatch_kFileIOBufBetterSize
    #   define hpatch_kFileIOBufBetterSize  (1024*64)
    #endif
    
    #ifndef hpatch_kMaxPluginTypeLength
    #   define hpatch_kMaxPluginTypeLength   259
    #endif

    typedef struct hpatch_compressedDiffInfo{
        hpatch_StreamPos_t  newDataSize;
        hpatch_StreamPos_t  oldDataSize;
        hpatch_uint         compressedCount;//需要 打开 hpatch_decompressHandle 数量
        char                compressType[hpatch_kMaxPluginTypeLength+1]; //第三方实现细节。
    } hpatch_compressedDiffInfo;
    
    typedef void*  hpatch_decompressHandle;
    typedef enum{
        hpatch_dec_ok=0,
        hpatch_dec_mem_error,
        hpatch_dec_open_error,
        hpatch_dec_error,
        hpatch_dec_close_error,
    } hpatch_dec_error_t;
    typedef struct hpatch_TDecompress{
        hpatch_BOOL        (*is_can_open)(const char* compresseType);
        //错误 返回 0.
        hpatch_decompressHandle   (*open)(struct hpatch_TDecompress* decompressPlugin,
                                          hpatch_StreamPos_t dataSize,
                                          const struct hpatch_TStreamInput* codeStream,
                                          hpatch_StreamPos_t code_begin,
                                          hpatch_StreamPos_t code_end);//codeSize==code_end-code_begin
        hpatch_BOOL              (*close)(struct hpatch_TDecompress* decompressPlugin,
                                          hpatch_decompressHandle decompressHandle);
        //decompress_part() 必须 out (out_part_data_end-out_part_data), otherwise 错误 返回 hpatch_FALSE
        hpatch_BOOL    (*decompress_part)(hpatch_decompressHandle decompressHandle,
                                          unsigned char* out_part_data,unsigned char* out_part_data_end);
        //reset_code add 新 compressed 数据; 用于 支持 vcpatch, 可以 NULL
        hpatch_BOOL         (*reset_code)(hpatch_decompressHandle decompressHandle,
                                          hpatch_StreamPos_t dataSize,
                                          const struct hpatch_TStreamInput* codeStream,
                                          hpatch_StreamPos_t code_begin,
                                          hpatch_StreamPos_t code_end);
        volatile hpatch_dec_error_t decError; //如果 you 已使用 decError 值, once 补丁 必须 已使用 it's own hpatch_TDecompress
    } hpatch_TDecompress;
    #define _hpatch_update_decError(decompressPlugin,errorCode) \
        do { if ((decompressPlugin)->decError==hpatch_dec_ok)   \
                (decompressPlugin)->decError=errorCode;     } while(0)
    
    
    const hpatch_TStreamInput* mem_as_hStreamInput(hpatch_TStreamInput* out_stream,
                                                   const unsigned char* mem,const unsigned char* mem_end);
    const hpatch_TStreamOutput* mem_as_hStreamOutput(hpatch_TStreamOutput* out_stream,
                                                     unsigned char* mem,unsigned char* mem_end);
    
    hpatch_BOOL hpatch_deccompress_mem(hpatch_TDecompress* decompressPlugin,
                                       const unsigned char* code,const unsigned char* code_end,
                                       unsigned char* out_data,unsigned char* out_data_end);
    
    typedef struct{
        hpatch_TStreamInput         base;
        const hpatch_TStreamInput*  srcStream;
        hpatch_StreamPos_t          clipBeginPos;
    } TStreamInputClip;
    //clip srcStream 从 clipBeginPos 到 clipEndPos 作为 a 新 StreamInput;
    void TStreamInputClip_init(TStreamInputClip* self,const hpatch_TStreamInput*  srcStream,
                               hpatch_StreamPos_t clipBeginPos,hpatch_StreamPos_t clipEndPos);
    typedef struct{
        hpatch_TStreamOutput        base;
        const hpatch_TStreamOutput* srcStream;
        hpatch_StreamPos_t          clipBeginPos;
    } TStreamOutputClip;
    //clip srcStream 从 clipBeginPos 到 clipEndPos 作为 a 新 StreamInput;
    void TStreamOutputClip_init(TStreamOutputClip* self,const hpatch_TStreamOutput*  srcStream,
                                hpatch_StreamPos_t clipBeginPos,hpatch_StreamPos_t clipEndPos);

    
    #define  hpatch_kMaxPackedUIntBytes ((sizeof(hpatch_StreamPos_t)*8+6)/7+1)
    hpatch_BOOL hpatch_packUIntWithTag(unsigned char** out_code,unsigned char* out_code_end,
                                       hpatch_StreamPos_t uValue,hpatch_uint highTag,const hpatch_uint kTagBit);
    hpatch_uint hpatch_packUIntWithTag_size(hpatch_StreamPos_t uValue,const hpatch_uint kTagBit);
    #define hpatch_packUInt(out_code,out_code_end,uValue) \
                hpatch_packUIntWithTag(out_code,out_code_end,uValue,0,0)
    #define hpatch_packUInt_size(uValue) hpatch_packUIntWithTag_size(uValue,0)
    
    hpatch_BOOL hpatch_unpackUIntWithTag(const unsigned char** src_code,const unsigned char* src_code_end,
                                         hpatch_StreamPos_t* result,const hpatch_uint kTagBit);
    #define hpatch_unpackUInt(src_code,src_code_end,result) \
                hpatch_unpackUIntWithTag(src_code,src_code_end,result,0)

    
    typedef struct hpatch_TCover{
        hpatch_StreamPos_t oldPos;
        hpatch_StreamPos_t newPos;
        hpatch_StreamPos_t length;
    } hpatch_TCover;
    typedef struct hpatch_TCover32{
        hpatch_uint32_t oldPos;
        hpatch_uint32_t newPos;
        hpatch_uint32_t length;
    } hpatch_TCover32;
    typedef struct hpatch_TCover_sz{
        size_t oldPos;
        size_t newPos;
        size_t length;
    } hpatch_TCover_sz;

    //opened 输入 covers
    typedef struct hpatch_TCovers{
        hpatch_StreamPos_t (*leave_cover_count)(const struct hpatch_TCovers* covers);
        //读取 out a cover,和 到 下一个 cover pos; 如果 错误 然后 返回 假
        hpatch_BOOL               (*read_cover)(struct hpatch_TCovers* covers,hpatch_TCover* out_cover);
        hpatch_BOOL                (*is_finish)(const struct hpatch_TCovers* covers);
        hpatch_BOOL                    (*close)(struct hpatch_TCovers* covers);
    } hpatch_TCovers;
    
    //输出 covers
    typedef struct hpatch_TOutputCovers{
        hpatch_BOOL (*push_cover)(struct hpatch_TOutputCovers* out_covers,const hpatch_TCover* cover); 
        void (*collate_covers)(struct hpatch_TOutputCovers* out_covers); // 用于 支持 search covers 通过 multi-线程
    } hpatch_TOutputCovers;
    
    typedef struct{
        hpatch_StreamPos_t  newDataSize;
        hpatch_StreamPos_t  oldDataSize;
        hpatch_StreamPos_t  uncompressedSize;
        hpatch_StreamPos_t  compressedSize;
        hpatch_StreamPos_t  diffDataPos;
        hpatch_StreamPos_t  coverCount;
        hpatch_StreamPos_t  stepMemSize;
        char                compressType[hpatch_kMaxPluginTypeLength+1]; //第三方实现细节。
    } hpatch_singleCompressedDiffInfo;

    hpatch_inline static void _singleDiffInfoToHDiffInfo(hpatch_compressedDiffInfo* out_diffInfo,const hpatch_singleCompressedDiffInfo* singleDiffInfo){
        out_diffInfo->newDataSize=singleDiffInfo->newDataSize;
        out_diffInfo->oldDataSize=singleDiffInfo->oldDataSize;
        out_diffInfo->compressedCount=(singleDiffInfo->compressedSize>0)?1:0;
        memcpy(out_diffInfo->compressType,singleDiffInfo->compressType,strlen(singleDiffInfo->compressType)+1);
    }
    
    typedef struct sspatch_listener_t{ 
        void*         import;   
        hpatch_BOOL (*onDiffInfo)(struct sspatch_listener_t* listener,
                                  const hpatch_singleCompressedDiffInfo* info,
                                  hpatch_TDecompress** out_decompressPlugin,//查找 decompressPlugin 通过 info->compressType
                                  unsigned char** out_temp_cache,    //*out_temp_cacheEnd-*out_temp_cache == info->stepMemSize + (I/O 缓存 内存)
                                  unsigned char** out_temp_cacheEnd);//  注意: (I/O 缓存 内存) >= hpatch_kStreamCacheSize*3
        void        (*onPatchFinish)(struct sspatch_listener_t* listener, //onPatchFinish 可以 null
                                     unsigned char* temp_cache, unsigned char* temp_cacheEnd);
    } sspatch_listener_t;

    typedef struct{
        hpatch_TStreamInput     base;
        hpatch_TDecompress*     _decompressPlugin;
        hpatch_decompressHandle _decompressHandle;
    } hpatch_TUncompresser_t;

    typedef struct sspatch_coversListener_t{
        void*         import;
        void        (*onStepCoversReset)(struct sspatch_coversListener_t* listener,hpatch_StreamPos_t leaveCoverCount);//可以 NULL, 数据(在 covers_cache) will 无效
        void        (*onStepCovers)(struct sspatch_coversListener_t* listener,
                                    const unsigned char* covers_cache,const unsigned char* covers_cacheEnd);
    } sspatch_coversListener_t;
    
    typedef struct{
        const unsigned char* covers_cache;
        const unsigned char* covers_cacheEnd;
        hpatch_StreamPos_t   lastOldEnd;
        hpatch_StreamPos_t   lastNewEnd;
        hpatch_TCover        cover;
    } sspatch_covers_t;

    hpatch_inline static void sspatch_covers_init(sspatch_covers_t* self) { memset(self,0,sizeof(*self)); }
    hpatch_inline static void sspatch_covers_setCoversCache(sspatch_covers_t* self,const unsigned char* covers_cache,const unsigned char* covers_cacheEnd){
                                    self->covers_cache=covers_cache; self->covers_cacheEnd=covers_cacheEnd; }
    hpatch_inline static hpatch_BOOL sspatch_covers_isHaveNextCover(const sspatch_covers_t* self) { return (self->covers_cache!=(self)->covers_cacheEnd); }

    hpatch_BOOL sspatch_covers_nextCover(sspatch_covers_t* self);
    
#ifdef __cplusplus
}
#endif
#endif
