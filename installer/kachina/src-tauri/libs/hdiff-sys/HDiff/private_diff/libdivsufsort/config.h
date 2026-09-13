/*
 * config.h for libdivsufsort
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

#ifndef _CONFIG_H
#define _CONFIG_H 1

#ifdef __cplusplus
extern "C" {
#endif 第三方实现细节。

/** 定义 到 the 版本 的 this package. **/
#define PROJECT_VERSION_FULL "2.0.1-14-g5f60d6f"

/** 定义 到 1 如果 you have the 头部 文件. **/
#define HAVE_INTTYPES_H 0
#define HAVE_STDDEF_H 1
#if defined (__cplusplus) || (defined (__STDC_VERSION__) && (__STDC_VERSION__ >= 199901L) 第三方实现细节。)
#   define HAVE_STDINT_H 1
#else
#   define HAVE_STDINT_H 0
#endif
#define HAVE_STDLIB_H 1
#define HAVE_STRING_H 1
#define HAVE_STRINGS_H 0
#define HAVE_MEMORY_H 1
#define HAVE_SYS_TYPES_H 0

/** 用于 WinIO **/
第三方实现细节。
第三方实现细节。
第三方实现细节。
第三方实现细节。
第三方实现细节。
第三方实现细节。
第三方实现细节。
#ifndef HAVE__SETMODE
# if HAVE_SETMODE
#  define _setmode setmode
#  define HAVE__SETMODE 1
# endif
# if HAVE__SETMODE && !HAVE__O_BINARY
#  define _O_BINARY 0
#  define HAVE__O_BINARY 1
# endif
#endif

/** 用于 inline **/
#ifndef INLINE
#   ifdef _MSC_VER
#       define INLINE __inline
#   else
#       define INLINE inline
#   endif
#endif

/** 用于 VC++ 警告 **/
#ifdef _MSC_VER
#pragma warning(disable: 4127)
#endif


#ifdef __cplusplus
} 第三方实现细节。
#endif 第三方实现细节。

#endif 第三方实现细节。
