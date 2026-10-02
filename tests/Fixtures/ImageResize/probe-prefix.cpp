// Shim: CC0-1.0. Included original functions: GPL-2.0-or-later.
#include <algorithm>
#include <cassert>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <vector>
struct Image {
 unsigned w=3,h=2; std::vector<unsigned char> bytes;
 unsigned width()const{return w;} unsigned height()const{return h;}
 unsigned char* scanLine(unsigned y){assert(y<h);return bytes.data()+size_t(y)*w*4;}
 const unsigned char* scanLine(unsigned y)const{assert(y<h);return bytes.data()+size_t(y)*w*4;}
 void setSize(unsigned width,unsigned height){assert(width>0&&height>0&&width*height<1000);w=width;h=height;bytes.assign(size_t(w)*h*4,0);}
 void flipHorizontal(){auto old=bytes;for(unsigned y=0;y<h;y++)for(unsigned x=0;x<w;x++)memcpy(scanLine(y)+x*4,old.data()+(size_t(y)*w+w-1-x)*4,4);}
 void flipVertical(){auto old=bytes;for(unsigned y=0;y<h;y++)memcpy(scanLine(y),old.data()+size_t(h-1-y)*w*4,w*4);}
 void rotate(int angle){int turns=((angle%360)+360)%360/90;assert(angle%90==0);while(turns--){auto old=bytes;auto ow=w,oh=h;w=oh;h=ow;bytes.assign(size_t(w)*h*4,0);for(unsigned y=0;y<oh;y++)for(unsigned x=0;x<ow;x++)memcpy(scanLine(ow-1-x)+y*4,old.data()+(size_t(y)*ow+x)*4,4);}}
};
struct History{int count=0;void push_back(int,Image* before,Image* after){++count;delete before;delete after;}};
struct CImgDiffBuffer{
 int m_nImages=1;Image m_imgOrig32[1];bool m_temporarilyTransformed=false;
 bool m_horizontalFlip[1]={false},m_verticalFlip[1]={false};int m_angle[1]={0};
 History m_undoRecords;int compares=0;void CompareImages(){++compares;}
