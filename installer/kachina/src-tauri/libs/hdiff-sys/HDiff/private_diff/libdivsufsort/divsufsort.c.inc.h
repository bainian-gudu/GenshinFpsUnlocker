/*
 * divsufsort.c for libdivsufsort
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

#include "divsufsort_private.h"
#include <vector>
#include "../../../libParallel/parallel_channel.h"
#if (_IS_USED_MULTITHREAD)
#include <thread>
#endif

/*- 私有 Functions -*/

#if (_IS_USED_MULTITHREAD)
struct mt_data_t{
    CHLocker            locker;
    const sauchar_t*    T;
    sastore_t*          SA;
    const saidx_t*      bucket_B;
    const sastore_t*    PAb;
    saidx_t             bufsize;
    saidx_t             n;
    saidx_t             m;
};

static void _sssort_thread(saint_t* c0,saint_t* c1,saidx_t* j,
                           sastore_t *buf,mt_data_t* mt){
    saidx_t k = 0;
    saidx_t l;
    const saidx_t*  bucket_B=mt->bucket_B;
    for(;;) {
        {
            CAutoLocker __autoLocker(mt->locker.locker);
            if(0 < (l = *j)) {
                saint_t d0 = *c0, d1 = *c1;
                do {
                k = BUCKET_BSTAR(d0, d1);
                if(--d1 <= d0) {
                    d1 = ALPHABET_SIZE - 1;
                    if(--d0 < 0) { break; }
                }
                } while(((l - k) <= 1) && (0 < (l = k)));
                *c0 = d0, *c1 = d1, *j = k;
            }
        }
        if(l == 0) { break; }
        sastore_t* SA=mt->SA;
        sssort(mt->T, mt->PAb, SA + k, SA + l,
               buf, mt->bufsize, 2, mt->n, *(SA + k) == (mt->m - 1));
    }
}
#endif

/* Sorts suffixes 的 类型 B*. */
static
saidx_t
sort_typeBstar(const sauchar_t *T, sastore_t* SA,
               saidx_t *bucket_A, saidx_t *bucket_B,
               saidx_t n,int threadNum) {
  sastore_t *PAb, *ISAb;
  saidx_t i, j, k, t, m;
  saint_t c0, c1;

  第三方实现细节。
  for(i = 0; i < BUCKET_A_SIZE; ++i) { bucket_A[i] = 0; }
  for(i = 0; i < BUCKET_B_SIZE; ++i) { bucket_B[i] = 0; }

  /* 数量 the 数量 的 occurrences 的 the first one 或 two 字符 的 each
     类型 A, B 和 B* 后缀. Moreover, store the beginning 位置 的 全部
     类型 B* suffixes 到 the 数组 SA. */
  for(i = n - 1, m = n, c0 = T[n - 1]; 0 <= i;) {
    /* 类型 A 后缀. */
    do { ++BUCKET_A(c1 = c0); } while((0 <= --i) && ((c0 = T[i]) >= c1));
    if(0 <= i) {
      /* 类型 B* 后缀. */
      ++BUCKET_BSTAR(c0, c1);
      SA[--m] = i;
      /* 类型 B 后缀. */
      for(--i, c1 = c0; (0 <= i) && ((c0 = T[i]) <= c1); --i, c1 = c0) {
        ++BUCKET_B(c0, c1);
      }
    }
  }
  m = n - m;
/*
note:
  A 类型 B* 后缀 是 lexicographically smaller than a 类型 B 后缀 that
  begins 含有 the same first two 字符.
*/

  /* 计算 the 索引 的 开始/结束 point 的 each bucket. */
  for(c0 = 0, i = 0, j = 0; c0 < ALPHABET_SIZE; ++c0) {
    t = i + BUCKET_A(c0);
    BUCKET_A(c0) = i + j; /* 开始 point */
    i = t + BUCKET_B(c0, c0);
    for(c1 = c0 + 1; c1 < ALPHABET_SIZE; ++c1) {
      j += BUCKET_BSTAR(c0, c1);
      BUCKET_BSTAR(c0, c1) = j; /* 结束 point */
      i += BUCKET_B(c0, c1);
    }
  }

  if(0 < m) {
    /* 排序 the 类型 B* suffixes 通过 their first two 字符. */
    PAb = SA + n - m; ISAb = SA + m;
    for(i = m - 2; 0 <= i; --i) {
      t = PAb[i], c0 = T[t], c1 = T[t + 1];
      SA[--BUCKET_BSTAR(c0, c1)] = i;
    }
    t = PAb[m - 1], c0 = T[t], c1 = T[t + 1];
    SA[--BUCKET_BSTAR(c0, c1)] = m - 1;

    /* 排序 the 类型 B* substrings using sssort. */
#if (_IS_USED_MULTITHREAD)
    if (threadNum>1){
        const saidx_t bufsize = (n - (2 * m)) / (saidx_t)threadNum;
        const int threadCount=threadNum-1;
        c0 = ALPHABET_SIZE - 2, c1 = ALPHABET_SIZE - 1, j = m;
        mt_data_t mt_data;
        mt_data.T=T;
        mt_data.SA=SA;
        mt_data.bucket_B=bucket_B;
        mt_data.PAb=PAb;
        mt_data.bufsize=bufsize;
        mt_data.n=n;
        mt_data.m=m;
        std::vector<std::thread> threads(threadCount);
        sastore_t* buf = SA + m;
        for (int ti=0;ti<threadCount;++ti,buf+=bufsize){
            threads[ti]=std::thread(_sssort_thread,&c0,&c1,&j,buf,&mt_data);
        }
        _sssort_thread(&c0,&c1,&j,buf,&mt_data);
        for (int ti=0;ti<threadCount;++ti)
            threads[ti].join();
    }else
#endif
    {
        sastore_t* buf = SA + m;
        saidx_t bufsize = n - (2 * m);
        for(c0 = ALPHABET_SIZE - 2, j = m; 0 < j; --c0) {
            for(c1 = ALPHABET_SIZE - 1; c0 < c1; j = i, --c1) {
                i = BUCKET_BSTAR(c0, c1);
                if(1 < (j - i)) {
                    sssort(T, PAb, SA + i, SA + j,
                           buf, bufsize, 2, n, *(SA + i) == (m - 1));
                }
            }
        }
    }

    /* Compute ranks 的 类型 B* substrings. */
    for(i = m - 1; 0 <= i; --i) {
      if(0 <= SA[i]) {
        j = i;
        do { ISAb[SA[i]] = i; } while((0 <= --i) && (0 <= SA[i]));
        SA[i + 1] = i - j;
        if(i <= 0) { break; }
      }
      j = i;
      do { ISAb[SA[i] = ~SA[i]] = j; } while(SA[--i] < 0);
      ISAb[SA[i]] = j;
    }

    /* Construct the inverse 后缀 数组 的 类型 B* suffixes using trsort. */
    trsort(ISAb, SA, m, 1);

    /* Set the sorted order 的 tyoe B* suffixes. */
    for(i = n - 1, j = m, c0 = T[n - 1]; 0 <= i;) {
      for(--i, c1 = c0; (0 <= i) && ((c0 = T[i]) >= c1); --i, c1 = c0) { }
      if(0 <= i) {
        t = i;
        for(--i, c1 = c0; (0 <= i) && ((c0 = T[i]) <= c1); --i, c1 = c0) { }
        SA[ISAb[--j]] = ((t == 0) || (1 < (t - i))) ? t : ~t;
      }
    }

    /* 计算 the 索引 的 开始/结束 point 的 each bucket. */
    BUCKET_B(ALPHABET_SIZE - 1, ALPHABET_SIZE - 1) = n; /* 结束 point */
    for(c0 = ALPHABET_SIZE - 2, k = m - 1; 0 <= c0; --c0) {
      i = BUCKET_A(c0 + 1) - 1;
      for(c1 = ALPHABET_SIZE - 1; c0 < c1; --c1) {
        t = i - BUCKET_B(c0, c1);
        BUCKET_B(c0, c1) = i; /* 结束 point */

        /* 移动 全部 类型 B* suffixes 到 the correct 位置. */
        for(i = t, j = BUCKET_BSTAR(c0, c1);
            j <= k;
            --i, --k) { SA[i] = SA[k]; }
      }
      BUCKET_BSTAR(c0, c0 + 1) = i - BUCKET_B(c0, c0) + 1; /* 开始 point */
      BUCKET_B(c0, c0) = i; /* 结束 point */
    }
  }

  return m;
}

/* Constructs the 后缀 数组 通过 using the sorted order 的 类型 B* suffixes. */
static
void
construct_SA(const sauchar_t *T, sastore_t* SA,
             saidx_t *bucket_A, saidx_t *bucket_B,
             saidx_t n, saidx_t m) {
  sastore_t *i, *j, *k;
  saidx_t s;
  saint_t c0, c1, c2;

  if(0 < m) {
    /* Construct the sorted order 的 类型 B suffixes 通过 using
       the sorted order 的 类型 B* suffixes. */
    for(c1 = ALPHABET_SIZE - 2; 0 <= c1; --c1) {
      /* Scan the 后缀 数组 从 right 到 left. */
      for(i = SA + BUCKET_BSTAR(c1, c1 + 1),
          j = SA + BUCKET_A(c1 + 1) - 1, k = NULL, c2 = -1;
          i <= j;
          --j) {
        if(0 < (s = *j)) {
          assert(T[s] == c1);
          assert(((s + 1) < n) && (T[s] <= T[s + 1]));
          assert(T[s - 1] <= T[s]);
          *j = ~s;
          c0 = T[--s];
          if((0 < s) && (T[s - 1] > c0)) { s = ~s; }
          if(c0 != c2) {
            if(0 <= c2) { BUCKET_B(c2, c1) =(saidx_t)(k - SA); }
            k = SA + BUCKET_B(c2 = c0, c1);
          }
          assert(k < j);
          *k-- = s;
        } else {
          assert(((s == 0) && (T[s] == c1)) || (s < 0));
          *j = ~s;
        }
      }
    }
  }

  /* Construct the 后缀 数组 通过 using
     the sorted order 的 类型 B suffixes. */
  k = SA + BUCKET_A(c2 = T[n - 1]);
  *k++ = (T[n - 2] < c2) ? ~(n - 1) : (n - 1);
  /* Scan the 后缀 数组 从 left 到 right. */
  for(i = SA, j = SA + n; i < j; ++i) {
    if(0 < (s = *i)) {
      assert(T[s - 1] >= T[s]);
      c0 = T[--s];
      if((s == 0) || (T[s - 1] < c0)) { s = ~s; }
      if(c0 != c2) {
        BUCKET_A(c2) = (saidx_t)(k - SA);
        k = SA + BUCKET_A(c2 = c0);
      }
      assert(i < k);
      *k++ = s;
    } else {
      assert(s < 0);
      *i = ~s;
    }
  }
}

/*---------------------------------------------------------------------------*/

/*- 函数 -*/

saint_t
divsufsort(const sauchar_t *T, sastore_t* SA, saidx_t n,int threadNum) {
  saidx_t *bucket_A, *bucket_B;
  saidx_t m;
  saint_t err = 0;

  /* 检查 arguments. */
  if((T == NULL) || (SA == NULL) || (n < 0)) { return -1; }
  else if(n == 0) { return 0; }
  else if(n == 1) { SA[0] = 0; return 0; }
  else if(n == 2) { m = (T[0] < T[1]); SA[m ^ 1] = 0, SA[m] = 1; return 0; }

  bucket_A = (saidx_t *)malloc(BUCKET_A_SIZE * sizeof(saidx_t));
  bucket_B = (saidx_t *)malloc(BUCKET_B_SIZE * sizeof(saidx_t));

  第三方实现细节。
  if((bucket_A != NULL) && (bucket_B != NULL)) {
    m = sort_typeBstar(T, SA, bucket_A, bucket_B, n, threadNum);
    construct_SA(T, SA, bucket_A, bucket_B, n, m);
  } else {
    err = -2;
  }

  free(bucket_B);
  free(bucket_A);

  return err;
}

const char *
divsufsort_version(void) {
  return PROJECT_VERSION_FULL;
}
