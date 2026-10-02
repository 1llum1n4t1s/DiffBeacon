// Original extracted functions: GPL. Custom shim: CC0-1.0.
#include <algorithm>
#include <cassert>
#include <cstring>
#include <iostream>
#include <vector>
#include <iomanip>
struct Image {
 unsigned w=0,h=0; std::vector<unsigned char> bytes;
 unsigned width() const {return w;} unsigned height() const {return h;}
 unsigned char* scanLine(unsigned y){assert(y<h);return bytes.data()+size_t(y)*w*4;}
 const unsigned char* scanLine(unsigned y)const{assert(y<h);return bytes.data()+size_t(y)*w*4;}
};
struct History {int count=0; void push_back(int,Image* oldbitmap,Image* newbitmap){++count;delete oldbitmap;delete newbitmap;}};
struct Buffer {
 int m_nImages=2; bool m_bRO[2]={false,false}; Image m_imgOrig32[2]; bool m_temporarilyTransformed=false; History m_undoRecords; int compares=0;
 void CompareImages(){++compares;}
 struct TemporaryTransformation {Buffer& b; TemporaryTransformation(Buffer& v):b(v){assert(!b.m_temporarilyTransformed);b.m_temporarilyTransformed=true;} ~TemporaryTransformation(){b.m_temporarilyTransformed=false;}};
	bool DeleteRectangle(int pane, int left, int top, int right, int bottom)
	{
		if (pane < 0 || pane >= m_nImages || m_bRO[pane])
			return false;

		Image *oldbitmap = new Image(m_imgOrig32[pane]);

		{
			TemporaryTransformation tmp(*this);
			for (unsigned i = top; i < static_cast<unsigned>(bottom); ++i)
			{
				unsigned char* scanline = m_imgOrig32[pane].scanLine(i);
				memset(scanline + left * 4, 0, (right - left) * 4);
			}
		}

		Image *newbitmap = new Image(m_imgOrig32[pane]);
		m_undoRecords.push_back(pane, oldbitmap, newbitmap);

		CompareImages();
		return true;
	}
	void PasteImage(int pane, int x, int y, const Image& image)
	{
		if (pane < 0 || pane >= m_nImages)
			return;

		Image *oldbitmap = new Image(m_imgOrig32[pane]);
		{
			TemporaryTransformation tmp(*this);
			PasteImageInternal(pane, x, y, image);
		}
		Image *newbitmap = new Image(m_imgOrig32[pane]);
		m_undoRecords.push_back(pane, oldbitmap, newbitmap);
		CompareImages();
	}
	void PasteImageInternal(int pane, int x, int y, const Image& image)
	{
		assert(m_temporarilyTransformed);
		if (pane < 0 || pane >= m_nImages)
			return;

		int width = m_imgOrig32[pane].width();
		int height = m_imgOrig32[pane].height();
		if (width == 0 || height == 0)
			return;

		int left = std::clamp(x, 0, width - 1);
		int top = std::clamp(y, 0, height - 1);
		int right = std::clamp(static_cast<int>(x + image.width()), 0, width);
		int bottom = std::clamp(static_cast<int>(y + image.height()), 0, height);
		if (right - left <= 0)
			return;
		if (bottom - top <= 0)
			return;

		for (int i = top; i < bottom; ++i)
			memcpy(m_imgOrig32[pane].scanLine(i) + left * 4, 
				image.scanLine(i - y) + (left - x) * 4, (right - left) * 4);
	}
};
static void readImage(Image& im){std::cin>>im.w>>im.h;im.bytes.resize(size_t(im.w)*im.h*4);for(auto& v:im.bytes){int n;std::cin>>n;v=(unsigned char)n;}}
int main(){int count;std::cin>>count;for(int n=0;n<count;++n){std::string id,op;int pane,ro,a,b,c,d;std::cin>>id>>op>>pane>>ro>>a>>b>>c>>d;Buffer buf;buf.m_bRO[0]=buf.m_bRO[1]=(ro!=0);readImage(buf.m_imgOrig32[0]);buf.m_imgOrig32[1]=buf.m_imgOrig32[0];Image source;readImage(source);int result=-1;if(op=="delete")result=buf.DeleteRectangle(pane,a,b,c,d);else buf.PasteImage(pane,a,b,source);std::cout<<id<<" "<<result<<" "<<buf.m_undoRecords.count<<" "<<buf.compares<<" ";for(auto v:buf.m_imgOrig32[0].bytes)std::cout<<std::hex<<std::setw(2)<<std::setfill('0')<<(int)v;std::cout<<std::dec<<"\n";}return std::cin?0:3;}
