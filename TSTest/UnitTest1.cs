using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace TSTest
{
    [TestClass]
    public class UnitTest1
    {
        [TestMethod]
        public void TestMethod1()
        {
            string file = @"D:\Users\Administrator\Desktop\1_6课课本.pdf";
            //string file = @"D:\Users\Administrator\Desktop\2.pdf";
            string fname = Path.GetFileNameWithoutExtension(file);
            var document = PdfiumViewer.PdfDocument.Load(file);
            if (document != null)
            {
                int pageCount = document.PageCount;
                pageCount = 1;
                for (var index = 0; index < pageCount; index++)
                {
                    List<Image> images = document.GetPageImages(index);
                    if (images.Count > 0)
                    {
                        for(var i = 0; i < images.Count; i++)
                        {
                            Image image = images[i];
                            image.Save($"D:\\Users\\Administrator\\Desktop\\imgs\\{fname}_{index+1}_{i+1}.png", ImageFormat.Png);
                            image.Dispose();
                        }
                    }
                }
            }
        }
    }
}
