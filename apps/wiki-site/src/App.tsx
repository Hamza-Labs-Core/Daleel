import { Routes, Route } from 'react-router';
import Layout from './components/Layout';
import HomePage from './components/HomePage';
import MarkdownPage from './components/MarkdownPage';

// The Daleel wiki is a content-driven SPA: every page except Home is a markdown
// file under public/content/, rendered by MarkdownPage and listed in the sidebar
// via content/manifest.json. The catch-all route maps /<slug> -> content/<slug>.md.
export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<HomePage />} />
        <Route path="*" element={<MarkdownPage />} />
      </Route>
    </Routes>
  );
}
