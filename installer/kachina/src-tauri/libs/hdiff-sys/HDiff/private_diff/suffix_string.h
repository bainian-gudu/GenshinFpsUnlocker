//suffix_string.h
//后缀字符串的一个实现.
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

#ifndef __SUFFIX_STRING_H_
#define __SUFFIX_STRING_H_
#include <vector>
#include <stddef.h> //用于 ptrdiff_t,size_t
#ifndef _SSTRING_FAST_MATCH
#   define _SSTRING_FAST_MATCH 5
#endif
#if (_SSTRING_FAST_MATCH>0)
#   if (_SSTRING_FAST_MATCH<2)
#       error must _SSTRING_FAST_MATCH>=2!
#   endif
#   include "limit_mem_diff/bloom_filter.h"
#   include "limit_mem_diff/adler_roll.h"
#endif

#if defined (__cplusplus) || (defined (__STDC_VERSION__) && (__STDC_VERSION__ >= 199901L) 第三方实现细节。)
#   include <stdint.h> //用于 int32_t
namespace hdiff_private{
#else
namespace hdiff_private{
#   if (_MSC_VER >= 1300)
    typedef signed __int32 int32_t;
#   else
    typedef signed int     int32_t;
#   endif
#endif

#if (_SSTRING_FAST_MATCH>0)
class TFastMatchForSString{
public:
    typedef uint32_t      THash;
    typedef unsigned char TChar;
    enum { kFMMinStrSize=_SSTRING_FAST_MATCH };

    inline TFastMatchForSString(){}
    inline void clear(){ bf.clear(); }
    void buildMatchCache(const TChar* src_begin,const TChar* src_end,size_t threadNum);

    static inline THash getHash(const TChar* datas) { return fast_adler32_start(datas,kFMMinStrSize); }
    static inline THash rollHash(THash h,const TChar* cur) { return fast_adler32_roll(h,kFMMinStrSize,cur[-kFMMinStrSize],cur[0]); }

    inline bool isHit(THash h) const { return bf.is_hit(h); }
private:
    TBloomFilter<THash>  bf;
};
#endif

class TSuffixString{
public:
    typedef ptrdiff_t     TInt;
    typedef int32_t       TInt32;
    typedef unsigned char TChar;
    explicit TSuffixString(bool isUsedFastMatch=false);
    ~TSuffixString();
    
    //throw std::runtime_error 当 创建 SA 错误
    TSuffixString(const TChar* src_begin,const TChar* src_end,bool isUsedFastMatch=false,size_t threadNum=1);
    void resetSuffixString(const TChar* src_begin,const TChar* src_end,size_t threadNum=1);

    inline const TChar* src_begin()const{ return m_src_begin; }
    inline const TChar* src_end()const{ return m_src_end; }
    inline size_t SASize()const{ return (size_t)(m_src_end-m_src_begin); }
    void clear();

    inline TInt SA(TInt i)const{//返回 m_SA[i];//排好序的后缀字符串数组.
        if (isUseLargeSA())
            return m_SA_large[i];
        else
            return (TInt)m_SA_limit[i];
    }
    TInt lower_bound(const TChar* str,const TChar* str_end)const;//返回 索引 在 SA; 必须 str_end-str>=2 !
private:
    TSuffixString(const TSuffixString &); //empty
    TSuffixString &operator=(const TSuffixString &); //empty
private:
    const TChar*        m_src_begin;//原字符串.
    const TChar*        m_src_end;
    std::vector<TInt32> m_SA_limit;
    std::vector<TInt>   m_SA_large;
    enum{ kLimitSASize= (1<<30)-1 + (1<<30) };//2G-1
    inline bool isUseLargeSA()const{
        return (sizeof(TInt)>sizeof(TInt32)) && (SASize()>kLimitSASize);
    }
private:
    // 全部 缓存 用于 lower_bound speed
    const bool              m_isUsedFastMatch;
#if (_SSTRING_FAST_MATCH>0)
    TFastMatchForSString    m_fastMatch; //a big 内存 缓存 & build slow
#endif
    const void*         m_cached_SA_begin;
    const void*         m_cached_SA_end;
    const void*         m_cached1char_range[256+1];
    void*               m_cached2char_range;//[256*256+1]
    typedef TInt (*t_lower_bound_func)(const void* rbegin,const void* rend,
                                       const TChar* str,const TChar* str_end,
                                       const TChar* src_begin,const TChar* src_end,
                                       const void* SA_begin,size_t min_eq);
    t_lower_bound_func  m_lower_bound;
    void                build_cache(size_t threadNum);
    void                clear_cache();
};

}//命名空间 hdiff_private
#endif //__SUFFIX_STRING_H_
