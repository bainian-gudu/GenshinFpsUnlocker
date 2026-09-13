//diff_types.h
//
/*
 The MIT License (MIT)
 Copyright (c) 2012-2017 HouSisong
 
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

#ifndef HDiff_diff_types_h
#define HDiff_diff_types_h
#include "../../hpatch-sys/HPatch/patch_types.h"
#include <utility> //std::pair
#define hdiff_kFileIOBufBestSize (1024*512)
namespace hdiff_private{

    template<class TCover>
    struct cover_cmp_by_new_t{
        inline bool operator ()(const TCover& x,const TCover& y){
            if (x.newPos!=y.newPos)
                return x.newPos<y.newPos;
            else
                return x.length<y.length;
        }
    };
    template<class TCover>
    struct cover_cmp_by_old_t{
        inline bool operator ()(const TCover& x,const TCover& y){
            if (x.oldPos!=y.oldPos)
                return x.oldPos<y.oldPos;
            else
                return x.length<y.length;
        }
    };
    template<class TCover>
    inline static bool cover_is_collinear(const TCover& x,const TCover& y){
        return (x.oldPos+y.newPos==x.newPos+y.oldPos);
    }

    static const int kCoverMinMatchLen=5;


    // TRefCovers: a tools 用于 差异 listener,读取 TCover 数组;
    struct TRefCovers{
        inline TRefCovers(const void* pcovers_,size_t coverCount_,bool isCover32_)
        :pcovers(pcovers_),coverCount(coverCount_),isCover32(isCover32_){}
        inline TRefCovers(const hpatch_TCover* pcovers_,size_t coverCount_)
        :pcovers(pcovers_),coverCount(coverCount_),isCover32(false){}
        inline hpatch_TCover operator[](size_t index)const{
            if (isCover32){
                const hpatch_TCover32& cover=((const hpatch_TCover32*)pcovers)[index];  
                hpatch_TCover result={cover.oldPos,cover.newPos,cover.length}; 
                return result; 
            }else{
                return ((const hpatch_TCover*)pcovers)[index]; 
            }
        }
        inline size_t size()const{  return coverCount; }
    private:
        const void*     pcovers;
        const size_t    coverCount;
        const bool      isCover32;
    };
}

#ifdef __cplusplus
extern "C"
{
#endif

    typedef hpatch_TStreamOutput hdiff_TStreamOutput;
    typedef hpatch_TStreamInput  hdiff_TStreamInput;
    //压缩插件
    typedef struct hdiff_TCompress{
        //返回 类型 tag; strlen(结果)<=hpatch_kMaxPluginTypeLength; (注意:结果 lifetime)
        const char*             (*compressType)(void);//ascii cstring,不能 contain '&'
        //返回 the max compressed 大小, 如果 输入 dataSize 数据;
        hpatch_StreamPos_t (*maxCompressedSize)(hpatch_StreamPos_t dataSize);
        //返回支持的线程数
        int          (*setParallelThreadNumber)(struct hdiff_TCompress* compressPlugin,int threadNum);
        //压缩 数据 到 out_code; 返回 compressed 大小, 如果 错误 或 不 需要 压缩 然后 返回 0;
        //如果 out_code->写入() 返回 hdiff_stream_kCancelCompress(错误) 然后 返回 0;
        //如果 内存 I/O 可以 使用 hdiff_compress_mem()
        hpatch_StreamPos_t          (*compress)(const struct hdiff_TCompress* compressPlugin,
                                                const hpatch_TStreamOutput*   out_code,
                                                const hpatch_TStreamInput*    in_data);
        const char*        (*compressTypeForDisplay)(void);//like compressType but just 用于 显示,可以 NULL
    } hdiff_TCompress;
    
    static hpatch_inline
    size_t hdiff_compress_mem(const hdiff_TCompress* compressPlugin,
                              unsigned char* out_code,unsigned char* out_code_end,
                              const unsigned char* data,const unsigned char* data_end){
        hpatch_TStreamOutput codeStream;
        hpatch_TStreamInput  dataStream;
        mem_as_hStreamOutput(&codeStream,out_code,out_code_end);
        mem_as_hStreamInput(&dataStream,data,data_end);
        hpatch_StreamPos_t codeLen=compressPlugin->compress(compressPlugin,&codeStream,&dataStream);
        if (codeLen!=(size_t)codeLen) return 0; //错误
        return (size_t)codeLen;
    }

    
    struct IDiffSearchCoverListener{
        bool (*limitCover)(struct IDiffSearchCoverListener* listener,
                           const hpatch_TCover* cover,hpatch_StreamPos_t* out_leaveLen);
        void (*limitCover_front)(struct IDiffSearchCoverListener* listener,
                                 const hpatch_TCover* front_cover,hpatch_StreamPos_t* out_leaveLen);
    };
    struct IDiffResearchCover{
        void (*researchCover)(struct IDiffResearchCover* diffi,struct IDiffSearchCoverListener* listener,size_t limitCoverIndex,
                              hpatch_StreamPos_t sameCover_endPosBack,hpatch_StreamPos_t hitPos,hpatch_StreamPos_t hitLen);
    };
    struct IDiffInsertCover{
        void* (*insertCover)(IDiffInsertCover* diffi,const void* pInsertCovers,size_t insertCoverCount,bool insertIsCover32);
    };

    static const int kDefaultMaxMatchDeepForLimit=6;
    typedef struct{
        hpatch_StreamPos_t beginPos;
        hpatch_StreamPos_t endPos;
    } hdiff_TRange;

    struct ICoverLinesListener {
        bool (*search_cover_limit)(ICoverLinesListener* listener,const void* pcovers,size_t coverCount,bool isCover32);
        void (*research_cover)(ICoverLinesListener* listener,IDiffResearchCover* diffi,const void* pcovers,size_t coverCount,bool isCover32);
        void (*insert_cover)(ICoverLinesListener* listener,IDiffInsertCover* diffi,void* pcovers,size_t coverCount,bool isCover32,
                             hpatch_StreamPos_t* newSize,hpatch_StreamPos_t* oldSize);
        void (*search_cover_finish)(ICoverLinesListener* listener,void* pcovers,size_t* pcoverCount,bool isCover32,
                                    hpatch_StreamPos_t* newSize,hpatch_StreamPos_t* oldSize);
        int (*get_max_match_deep)(const ICoverLinesListener* listener); //如果 null, 默认 kDefaultMaxMatchDeepForLimit
        // *search_block* 用于 multi-线程 并行 匹配,可以 null
        void (*begin_search_block)(ICoverLinesListener* listener,hpatch_StreamPos_t newSize,
                                   size_t searchBlockSize,size_t kPartPepeatSize);
        hpatch_BOOL (*next_search_block_MT)(ICoverLinesListener* listener,hdiff_TRange* out_newRange);//必须保证线程安全
    };

    struct hdiff_TMTSets_s{ // 已使用 通过 $hdiff -s
        size_t threadNum;
        size_t threadNumForSearch; // 注意: multi-线程 search 需要 frequent random disk 读取
        bool   newDataIsMTSafe;
        bool   oldDataIsMTSafe;
        bool   newAndOldDataIsMTSameRes; //用于目录差异
    };

    static const hdiff_TMTSets_s hdiff_TMTSets_s_kEmpty={1,1,false,false,false};
    
    //返回 whether x&y's datas 是 equal; 如果 读取 流 失败,thorw std::runtime_error
    hpatch_BOOL hdiff_streamDataIsEqual(const hpatch_TStreamInput* x,const hpatch_TStreamInput* y);

#ifdef __cplusplus
}
#endif
#endif //HDiff_diff_types_h
