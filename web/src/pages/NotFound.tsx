import { Link } from 'react-router';
import { Compass, Lock } from 'lucide-react';
import { Card, Empty } from '../components/ui';

export function NotFoundPage({ forbidden = false }: { forbidden?: boolean }) {
  return (
    <Card>
      <Empty title={forbidden ? 'You don’t have access to this page' : 'Page not found'} icon={forbidden ? <Lock size={22} /> : <Compass size={22} />}>
        {forbidden ? 'This area is restricted to administrators.' : 'The page you requested does not exist.'}
        <div style={{ marginTop: 14 }}><Link to="/" className="btn btn-primary">Back to overview</Link></div>
      </Empty>
    </Card>
  );
}
