#pragma once

#ifdef __cplusplus
#define EXTERNC extern "C"
#else
#define EXTERNC
#endif 

#define CallingConvention _cdecl

#ifdef DLL_IMPORT
#define HEAD EXTERNC __declspec(dllimport)
#else
#define HEAD EXTERNC __declspec(dllexport)
#endif

HEAD void CallingConvention Test1();

HEAD void CallingConvention TestLog(char* log);

HEAD void CallingConvention AddArrays(double* a, double* b, double* c, int length); 

HEAD void CallingConvention AddArraysParallel(double* a, double* b, double* c, int length);



