///////////////////////// ankerl::unordered_dense::{map, set} /////////////////////////

// Standard library includes for <ankerl/unordered_dense.h>.
//
// <ankerl/unordered_dense.h> includes this header unless the consumer defines
// ANKERL_UNORDERED_DENSE_STD_MODULE=1 first, which is what a C++20 modules build does:
// the standard library then arrives as `import std;` from the consumer, so nothing here
// is needed and nothing here is included. Splitting the two is the point of the header.
//

// A fast & densely stored hashmap and hashset.
// Version 5.0.1
// https://github.com/martinus/unordered_dense
//
// Licensed under the MIT License <http://opensource.org/licenses/MIT>.
// SPDX-License-Identifier: MIT
// Copyright (c) 2022 Martin Leitner-Ankerl <martin.ankerl@gmail.com>
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

#ifndef ANKERL_UNORDERED_DENSE_STL_H
#define ANKERL_UNORDERED_DENSE_STL_H

#include <algorithm>        // for (std::min), (std::max)
#include <array>            // for std::array
#include <cstddef>          // for std::size_t
#include <cstdint>          // for std::uint8_t, std::uint32_t, std::uint64_t, std::uintptr_t
#include <cstdlib>          // for abort
#include <cstring>          // for std::memcpy
#include <functional>       // for std::equal_to, std::hash
#include <initializer_list> // for std::initializer_list
#include <iterator>         // for std::distance, std::iterator_traits, iterator tags
#include <limits>           // for std::numeric_limits
#include <memory>           // for std::allocator, std::allocator_traits, std::shared_ptr, std::destroy
#include <optional>         // for std::optional
#include <stdexcept>        // for std::logic_error, std::out_of_range, std::overflow_error
#include <string>           // for std::basic_string, std::string
#include <string_view>      // for std::basic_string_view, std::string_view
#include <tuple>            // for std::tuple, std::get, std::forward_as_tuple, std::tuple_size_v
#include <type_traits>      // for std::enable_if_t, std::conditional_t, std::is_*_v, std::void_t
#include <utility>          // for std::pair, std::move, std::forward, std::swap, std::exchange
#include <vector>           // for std::vector

// The pmr aliases at the bottom of <ankerl/unordered_dense.h> are guarded by this macro, so it is
// spelled here: <memory_resource> exists from C++17 on, and older toolchains only ever had the
// experimental spelling. Defining it by hand keeps the aliases available either way.
#if !defined(ANKERL_UNORDERED_DENSE_PMR)
#    if defined(__has_include)
#        if __has_include(<memory_resource>)
#            define ANKERL_UNORDERED_DENSE_PMR std::pmr // NOLINT(cppcoreguidelines-macro-usage)
#            include <memory_resource>                 // for std::pmr::polymorphic_allocator
#        elif __has_include(<experimental/memory_resource>)
#            define ANKERL_UNORDERED_DENSE_PMR std::experimental::pmr // NOLINT(cppcoreguidelines-macro-usage)
#            include <experimental/memory_resource>                   // for polymorphic_allocator
#        endif
#    endif
#endif

#endif // ANKERL_UNORDERED_DENSE_STL_H
