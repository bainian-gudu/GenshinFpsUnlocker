/*
 * divsufsort.h for libdivsufsort
 * Copyright (c) 2003-2008 Yuta Mori All Rights Reserved.
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 */

#ifndef _DIVSUFSORT_H
#define _DIVSUFSORT_H 1

#if defined (__cplusplus) || (defined (__STDC_VERSION__) && (__STDC_VERSION__ >= 199901L) 第三方实现细节。)
#   include <stdint.h> //用于 uint8_t,int32_t
#else
#   if (_MSC_VER >= 1300)
    typedef unsigned __int8     uint8_t;
    typedef signed __int32      int32_t;
#   else
    typedef unsigned char       uint8_t;
    typedef signed int          int32_t;
#   endif
#endif

#ifdef __cplusplus
extern "C" {
#endif 第三方实现细节。

#ifndef PRId32
#   define PRId32 "d"
#endif

#ifndef DIVSUFSORT_API
# ifdef DIVSUFSORT_BUILD_DLL
#  define DIVSUFSORT_API 
# else
#  define DIVSUFSORT_API 
# endif
#endif

第三方实现细节。
#ifndef SAUCHAR_T
#define SAUCHAR_T
typedef uint8_t sauchar_t;
#endif 第三方实现细节。
#ifndef SAINT_T
#define SAINT_T
typedef int32_t saint_t;
#endif 第三方实现细节。
#ifndef SAIDX32_T
#define SAIDX32_T
typedef int32_t saidx32_t;
#endif 第三方实现细节。

第三方实现细节。

/**
 * Constructs the 后缀 数组 的 a given 字符串.
 * @param T[0..n-1] The 输入 字符串.
 * @param SA[0..n-1] The 输出 数组 的 suffixes.
 * @param n The 长度 的 the given 字符串.
 * @返回 0 如果 no 错误 occurred, -1 或 -2 otherwise.
 */
DIVSUFSORT_API
saint_t
divsufsort(const sauchar_t *T,saidx32_t *SA,saidx32_t n,int threadNum);

/**
 * Returns the 版本 的 the divsufsort library.
 * @返回 The 版本 数量 字符串.
 */
DIVSUFSORT_API
const char *
divsufsort_version(void);

#ifdef __cplusplus
} 第三方实现细节。
#endif 第三方实现细节。

#endif 第三方实现细节。
