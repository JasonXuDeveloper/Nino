#ifndef DEFLATE_LIBRARY_H
#define DEFLATE_LIBRARY_H

#include <stdint.h>

#ifdef _WIN32
#define MODULE_API_EXPORTS
#  ifdef MODULE_API_EXPORTS
#    define MODULE_API __declspec(dllexport)
#  else
#    define MODULE_API __declspec(dllimport)
#  endif
#else
#  define MODULE_API
#endif

//LINUX MIGHT NEED THIS (if can not cmake and outputs z_size_t is undefined, you can uncomment the below code
//#ifdef Z_SOLO
//typedef unsigned long z_size_t;
//#else
//#  define z_longlong long long
//#  if defined(NO_SIZE_T)
//typedef unsigned NO_SIZE_T z_size_t;
//#  elif defined(STDC)
//#    include <stddef.h>
//     typedef size_t z_size_t;
//#  else
//typedef unsigned long z_size_t;
//#  endif
//#  undef z_longlong
//#endif

#ifdef __builtin_va_list
#ifndef _VA_LIST
        typedef __builtin_va_list va_list;
        #define _VA_LIST
#endif
#endif

extern "C"
{
typedef int32_t (*read_write_func)(intptr_t buffer, int32_t length, intptr_t gchandle);

struct ZStream;

intptr_t MODULE_API CreateZStream(int32_t compress, uint8_t gzip, read_write_func func, intptr_t gchandle);
int32_t MODULE_API CloseZStream(intptr_t zstream);
int32_t MODULE_API Flush(intptr_t zstream);
int32_t MODULE_API ReadZStream(intptr_t zstream, intptr_t buffer, int32_t length);
int32_t MODULE_API WriteZStream(intptr_t zstream, intptr_t buffer, int32_t length);
}

#endif //DEFLATE_LIBRARY_H
